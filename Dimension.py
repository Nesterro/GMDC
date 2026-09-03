# -*- coding: utf-8 -*-
"""
Размеры от граней стен до осей коробов — каскадом за элементы.
- Горизонтальные короба -> каскад СНИЗУ, отсортированы по X слева направо
- Вертикальные короба   -> каскад СЛЕВА, отсортированы по Y снизу вверх
pyRevit / IronPython 2.7

Главные исправления против "выделяется не с первого раза":
  1. get_axis_ref переписан: несколько стратегий получения Reference оси
     с приоритетом, матчинг по КОЛЛИНЕАРНОСТИ и направлению, а не по
     точному совпадению концов (короткая геометрия фитингов больше не ломает поиск).
  2. Добавлен fallback на LocationCurve.Reference напрямую (надёжно для Conduit/CableTray).
  3. Поиск грани стены берёт расстояние от ближайшей точки оси короба, а не от midpoint —
     длинные вертикальные короба больше не отсекаются по MAX_DISTANCE.
  4. Глубокий обход GeometryInstance + поиск самой длинной коллинеарной линии.
"""
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')

from Autodesk.Revit.DB import (
    Transaction,
    FilteredElementCollector,
    BuiltInCategory,
    ViewType,
    Line,
    XYZ,
    ReferenceArray,
    HostObjectUtils,
    ShellLayerType,
    LocationCurve,
    Transform,
    RevitLinkInstance,
    Options,
    GeometryInstance,
    Dimension,
    DimensionType,
    DimensionStyleType,
    Plane,
    SketchPlane,
    DetailLine,
)

doc   = __revit__.ActiveUIDocument.Document
uidoc = __revit__.ActiveUIDocument

# ══════════════════════════════════════════════════════════════════════════════
# НАСТРОЙКИ
# ══════════════════════════════════════════════════════════════════════════════
MAX_DISTANCE_MM = 12000
FIRST_OFFSET_MM = 1500   # отступ первого размера от крайнего элемента
CASCADE_STEP_MM = 600    # шаг между уровнями каскада
CASCADE_LEVELS  = 4      # сколько уровней каскада (циклически), чтобы не уезжать вдаль
AXIS_TOL_MM     = 50     # допуск коллинеарности оси
ON_AXIS_TOL_MM  = 150    # допуск попадания линии на ось короба (половина ширины)
MIN_AXIS_LEN_MM = 100    # короче этого линия не считается осью (отсекает рёбра граней)

# Фильтры размеров:
MIN_DIM_MM      = 15     # размеры меньше этого значения не ставим (мусорные)
DEDUP_TOL_MM    = 2      # размеры с одинаковым значением (± этот допуск) в одном
                         # направлении не дублируем — оставляем только первый

# Диагностика: True -> НЕ строит размеры, только печатает что нашлось по каждому коробу.
# Прогони сначала с DRY_RUN=True, посмотри лог, потом ставь False.
DRY_RUN = False

MEP_CATS = [
    BuiltInCategory.OST_Conduit,
    BuiltInCategory.OST_CableTray,
    BuiltInCategory.OST_PipeCurves,
    BuiltInCategory.OST_DuctCurves,
]

def mm_to_ft(mm): return mm / 304.8

def get_aligned_dimtype():
    """Находит линейный (Aligned) тип размера. Для прямых стен Linear=Aligned визуально,
    но Revit различает их по StyleType. Берём первый Linear/Aligned тип."""
    best = None
    for dt in FilteredElementCollector(doc).OfClass(DimensionType).ToElements():
        try:
            st = dt.StyleType
        except Exception:
            continue
        if st == DimensionStyleType.Linear:
            return dt          # линейный (он же aligned для прямой линии) — то что нужно
        if best is None:
            best = dt
    return best

def get_view():
    v = doc.ActiveView
    if v.ViewType not in (ViewType.FloorPlan, ViewType.CeilingPlan,
                          ViewType.AreaPlan, ViewType.EngineeringPlan):
        raise Exception("Откройте план этажа и запустите снова.")
    return v

# ══════════════════════════════════════════════════════════════════════════════
# СБОР
# ══════════════════════════════════════════════════════════════════════════════
def collect_mep(view):
    result = []
    for cat in MEP_CATS:
        try:
            found = list(FilteredElementCollector(doc, view.Id)
                         .OfCategory(cat).WhereElementIsNotElementType().ToElements())
            if not found:
                found = list(FilteredElementCollector(doc)
                             .OfCategory(cat).WhereElementIsNotElementType().ToElements())
            if found:
                print("  MEP {}: {} шт.".format(cat, len(found)))
            result.extend(found)
        except Exception as ex:
            print("  [!] {}: {}".format(cat, ex))
    return result

def collect_walls():
    walls = []
    for w in (FilteredElementCollector(doc)
              .OfCategory(BuiltInCategory.OST_Walls)
              .WhereElementIsNotElementType().ToElements()):
        walls.append({'wall': w, 'xf': Transform.Identity, 'link': None, 'ldoc': None})
    for lnk in (FilteredElementCollector(doc).OfClass(RevitLinkInstance).ToElements()):
        ldoc = lnk.GetLinkDocument()
        if ldoc is None: continue
        xf = lnk.GetTotalTransform()
        found = list(FilteredElementCollector(ldoc)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType().ToElements())
        if found:
            print("  Стены из '{}': {}".format(ldoc.Title, len(found)))
        for w in found:
            walls.append({'wall': w, 'xf': xf, 'link': lnk, 'ldoc': ldoc})
    return walls

def get_existing_dim_elem_ids(view):
    done = set()
    for dim in (FilteredElementCollector(doc, view.Id).OfClass(Dimension).ToElements()):
        try:
            refs = dim.References
            if refs is None: continue
            for r in refs:
                eid = r.ElementId
                if eid is not None: done.add(eid.IntegerValue)
        except Exception:
            continue
    return done

# ══════════════════════════════════════════════════════════════════════════════
# ГЕОМЕТРИЯ
# ══════════════════════════════════════════════════════════════════════════════
def are_parallel(v1, v2, tol=0.10):
    return abs(v1.DotProduct(v2)) > (1.0 - tol)

def is_horizontal(mdir):
    return abs(mdir.X) >= 0.5

def get_wall_dir(wi):
    loc = wi['wall'].Location
    if not isinstance(loc, LocationCurve): return None
    c  = loc.Curve
    p0 = wi['xf'].OfPoint(c.GetEndPoint(0))
    p1 = wi['xf'].OfPoint(c.GetEndPoint(1))
    d  = XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0)
    return d.Normalize() if d.GetLength() > 1e-9 else None

def get_wall_face(wi, pt_world):
    wall     = wi['wall']
    pt_local = wi['xf'].Inverse.OfPoint(pt_world) if wi['link'] else pt_world
    best_ref  = None
    best_dist = float('inf')
    for shell in (ShellLayerType.Exterior, ShellLayerType.Interior):
        try:
            for fref in HostObjectUtils.GetSideFaces(wall, shell):
                face = wall.GetGeometryObjectFromReference(fref)
                if face is None: continue
                proj = face.Project(pt_local)
                d = proj.Distance if proj else float('inf')
                if d < best_dist:
                    best_dist = d
                    best_ref = fref.CreateLinkReference(wi['link']) if wi['link'] else fref
        except Exception:
            continue
    return best_ref, best_dist

def find_nearest_wall_any(walls, pt, max_ft):
    best_wi = best_ref = None
    best_dist = float('inf')
    for wi in walls:
        fref, dist = get_wall_face(wi, pt)
        if fref is None or dist > max_ft: continue
        if dist < best_dist:
            best_dist, best_wi, best_ref = dist, wi, fref
    return (best_wi, best_ref, best_dist) if best_wi else (None, None, None)

def find_wall_oriented(walls, pt, want_horizontal, max_ft):
    """Ближайшая стена нужной ориентации.
    want_horizontal=True -> стена идёт вдоль X (даёт размер по Y).
    want_horizontal=False -> стена идёт вдоль Y (даёт размер по X)."""
    best_wi = best_ref = None
    best_dist = float('inf')
    for wi in walls:
        wd = get_wall_dir(wi)
        if wd is None:
            continue
        wall_is_horizontal = abs(wd.X) >= 0.7
        if wall_is_horizontal != want_horizontal:
            continue
        fref, dist = get_wall_face(wi, pt)
        if fref is None or dist > max_ft:
            continue
        if dist < best_dist:
            best_dist, best_wi, best_ref = dist, wi, fref
    return (best_wi, best_ref, best_dist) if best_wi else (None, None, None)

def find_wall_parallel(walls, pt, mdir, max_ft):
    best_wi = best_ref = None
    best_dist = float('inf')
    for wi in walls:
        wd = get_wall_dir(wi)
        if wd is None or not are_parallel(mdir, wd): continue
        fref, dist = get_wall_face(wi, pt)
        if fref is None or dist > max_ft: continue
        if dist < best_dist:
            best_dist, best_wi, best_ref = dist, wi, fref
    return (best_wi, best_ref, best_dist) if best_wi else (None, None, None)

def _pt_to_line_dist(p, a, b):
    """Расстояние точки p до бесконечной прямой, заданной a->b (по XY)."""
    ab = XYZ(b.X - a.X, b.Y - a.Y, 0.0)
    L = ab.GetLength()
    if L < 1e-9:
        return XYZ(p.X - a.X, p.Y - a.Y, 0.0).GetLength()
    ab = ab.Normalize()
    ap = XYZ(p.X - a.X, p.Y - a.Y, 0.0)
    proj = ap.DotProduct(ab)
    foot = XYZ(a.X + ab.X * proj, a.Y + ab.Y * proj, 0.0)
    return XYZ(p.X - foot.X, p.Y - foot.Y, 0.0).GetLength()

def get_axis_ref(elem, curve):
    """
    Возвращает (Reference, источник) оси короба.

    Берём ТОЛЬКО линию, реально совпадающую с осью короба:
      - параллельна оси
      - ОБЕ конечные точки лежат на прямой оси (а не одна)
      - длиннее MIN_AXIS_LEN (отсекает короткие рёбра граней/фитингов)
    Среди подходящих — самая длинная.
    Если такой нет — НИКАКОГО слепого fallback на "самую длинную линию"
    (именно он раньше хватал грань короба = мусорные 13/3285мм).
    Последний и единственный fallback — Reference самого LocationCurve.
    """
    p0, p1 = curve.GetEndPoint(0), curve.GetEndPoint(1)
    raw = XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0)
    if raw.GetLength() < 1e-9:
        # короб вертикальный (стояк) — в плане точка. Reference оси нет.
        ref = _location_curve_ref(elem)
        if ref is not None:
            return ref, "LocCurve"
        return "NEED_LINE", "needLinePoint"
    cdir = raw.Normalize()
    minl = mm_to_ft(MIN_AXIS_LEN_MM)
    # центр оси ИМЕННО этого короба (по XY)
    mid  = XYZ((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5, 0.0)
    # порог попадания на ось: половина типичной ширины короба, чтобы не цеплять соседей,
    # но и не терять свой короб из-за смещения геометрии.
    tol  = mm_to_ft(ON_AXIS_TOL_MM)

    opt = Options()
    opt.ComputeReferences        = True
    opt.IncludeNonVisibleObjects = True
    opt.View = doc.ActiveView   # ОБЯЗАТЕЛЬНО: без View ComputeReferences не даёт
                                # валидных Reference у Conduit -> 0 размеров.

    geom = None
    try:
        geom = elem.get_Geometry(opt)
    except Exception:
        geom = None

    # Ищем линию, которая (а) параллельна оси, (б) лежит на ТОЙ ЖЕ прямой,
    # что и ось этого короба (центр короба near линии). Это исключает захват
    # референса соседнего параллельного короба -> уникальный axisRef на каждый.
    best_on_axis = [None, -1.0]    # [ref, length]
    best_any     = [None, -1.0]

    def consider(obj, xf):
        # Reference запоминаем СРАЗУ — до любых вычислений, которые могут упасть.
        # Так единственная линия с Reference (linesWithRef=1) никогда не теряется.
        if obj.Reference is None:
            return
        # без длины — приоритет по «есть Reference», длину оценим если получится
        try:
            q0 = xf.OfPoint(obj.GetEndPoint(0))
            q1 = xf.OfPoint(obj.GetEndPoint(1))
            d  = XYZ(q1.X - q0.X, q1.Y - q0.Y, 0.0)
            length = d.GetLength()
        except Exception:
            # точки не достать, но Reference есть — берём как кандидата с длиной 0
            if best_any[1] < 0:
                best_any[0], best_any[1] = obj.Reference, 0.0
            return
        # best_any ловит ЛЮБУЮ линию с Reference (без порога длины)
        if length > best_any[1]:
            best_any[0], best_any[1] = obj.Reference, length
        if length < minl:
            return
        ldir = d.Normalize()
        if not are_parallel(cdir, ldir):
            return
        if _pt_to_line_dist(mid, q0, q1) <= tol:
            if length > best_on_axis[1]:
                best_on_axis[0], best_on_axis[1] = obj.Reference, length

    def walk(objs, xf):
        # Плоский обход через стек — устойчивее рекурсии, не теряет линии
        # при падении в одной из веток (именно так Reference нашёлся в диагностике).
        if objs is None:
            return
        stack = [(o, xf) for o in objs]
        while stack:
            obj, cxf = stack.pop()
            if isinstance(obj, GeometryInstance):
                try:
                    inner = obj.GetInstanceGeometry()
                    if inner is not None:
                        for o in inner:
                            stack.append((o, cxf))
                except Exception:
                    pass
            elif isinstance(obj, Line):
                try:
                    consider(obj, cxf)
                except Exception:
                    try:
                        if obj.Reference is not None and best_any[1] < 0:
                            best_any[0], best_any[1] = obj.Reference, 0.0
                    except Exception:
                        pass

    if geom is not None:
        walk(geom, Transform.Identity)

    # ПРИОРИТЕТ:
    ref = _location_curve_ref(elem)
    if ref is not None:
        return ref, "LocCurve"
    if best_on_axis[0] is not None:
        return best_on_axis[0], "onAxis"
    if best_any[0] is not None:
        return best_any[0], "geomAny"
    try:
        r1 = curve.GetEndPointReference(1)
        if r1 is not None:
            return r1, "endRef"
    except Exception:
        pass
    # Reference оси не достаётся штатно -> пометка создать линию модели по оси.
    # Возвращаем спец-маркер, линия будет создана внутри транзакции.
    return "NEED_LINE", "needLine"

def _location_curve_ref(elem):
    try:
        loc = elem.Location
        if isinstance(loc, LocationCurve):
            c = loc.Curve
            if c is None:
                return None
            # 1) прямой Reference кривой
            if c.Reference is not None:
                return c.Reference
            # 2) endpoint-референсы (работают вне зависимости от секущего диапазона вида)
            for i in (0, 1):
                try:
                    r = c.GetEndPointReference(i)
                    if r is not None:
                        return r
                except Exception:
                    pass
    except Exception:
        pass
    return None

def ref_kind(ref):
    """Грубый тип reference для диагностики."""
    if ref is None:
        return "None"
    if isinstance(ref, str):
        return ref
    try:
        s = ref.ConvertToStableRepresentation(doc)
        return s[:40]
    except Exception:
        return str(ref.ElementReferenceType)

def nearest_pt_on_curve(curve, ref_pt):
    """Ближайшая точка оси короба к точке стены — для корректного замера дистанции."""
    try:
        proj = curve.Project(ref_pt)
        if proj is not None:
            return proj.XYZPoint
    except Exception:
        pass
    return curve.Evaluate(0.5, True)

def make_axis_line_ref(ecurve, pt, mdir):
    """Создаёт линию детали по оси короба (или поперёк, если короб точечный
    в плане — стояк) и возвращает её Reference. Вызывать ВНУТРИ транзакции."""
    try:
        z = pt.Z
        p0 = ecurve.GetEndPoint(0)
        p1 = ecurve.GetEndPoint(1)
        a = XYZ(p0.X, p0.Y, z)
        b = XYZ(p1.X, p1.Y, z)
        if a.DistanceTo(b) < mm_to_ft(10):
            # точечный в плане (стояк): строим короткую линию поперёк,
            # вдоль направления стены через точку короба
            u = XYZ(mdir.X, mdir.Y, 0.0)
            if u.GetLength() < 1e-9:
                u = XYZ(1.0, 0.0, 0.0)
            u = u.Normalize()
            h = mm_to_ft(100)
            a = XYZ(pt.X - u.X * h, pt.Y - u.Y * h, z)
            b = XYZ(pt.X + u.X * h, pt.Y + u.Y * h, z)
        line = Line.CreateBound(a, b)
        dl = doc.Create.NewDetailCurve(doc.ActiveView, line)
        if dl is None:
            return None
        geo = dl.GeometryCurve
        if geo is not None and geo.Reference is not None:
            return geo.Reference
        try:
            r = geo.GetEndPointReference(0)
            if r is not None:
                return r
        except Exception:
            pass
    except Exception:
        pass
    return None

def make_dim_line(pt, horiz, dim_pos, anchor, dist_ft):
    z      = pt.Z
    margin = mm_to_ft(500)
    if horiz:
        half = dist_ft + margin
        return Line.CreateBound(XYZ(anchor - half, dim_pos, z),
                                XYZ(anchor + half, dim_pos, z))
    else:
        return Line.CreateBound(XYZ(dim_pos, anchor - dist_ft - margin, z),
                                XYZ(dim_pos, anchor + margin, z))

# ══════════════════════════════════════════════════════════════════════════════
# MAIN
# ══════════════════════════════════════════════════════════════════════════════
def main():
    try:
        view = get_view()
    except Exception as e:
        print("ОШИБКА: {}".format(e)); return

    print("Вид: '{}'".format(view.Name))

    print("\n-- MEP --")
    mep_list = collect_mep(view)
    print("Итого: {}".format(len(mep_list)))

    print("\n-- Стены --")
    walls = collect_walls()
    print("Итого: {}".format(len(walls)))

    if not mep_list or not walls:
        print("Нет элементов. Стоп."); return

    already_done = get_existing_dim_elem_ids(view)
    print("Уже образмеренных: {}".format(len(already_done)))

    max_ft    = mm_to_ft(MAX_DISTANCE_MM)
    first_off = mm_to_ft(FIRST_OFFSET_MM)
    step      = mm_to_ft(CASCADE_STEP_MM)

    tasks_horiz = []
    tasks_vert  = []

    for elem in mep_list:
        try:
            if elem.Id.IntegerValue in already_done:
                continue
            loc = elem.Location
            if not isinstance(loc, LocationCurve):
                continue
            curve = loc.Curve
            pt    = curve.Evaluate(0.5, True)
            raw   = curve.GetEndPoint(1) - curve.GetEndPoint(0)
            mdir_raw = XYZ(raw.X, raw.Y, 0.0)
            mdir = mdir_raw.Normalize() if mdir_raw.GetLength() > 1e-9 else XYZ(1, 0, 0)

            # ось короба — общая для обоих размеров
            aref, asrc = get_axis_ref(elem, curve)
            if aref is None:
                print("  [ПРОПУСК] ID={}: нет оси".format(elem.Id))
                continue

            placed = 0

            # Размер по Y: до ближайшей ГОРИЗОНТАЛЬНОЙ стены (каскад вниз)
            wi_h, wref_h, dist_h = find_wall_oriented(walls, pt, True, max_ft)
            if wi_h is not None:
                # направление размера — вертикальное (поперёк горизонтальной стены)
                mdir_h = XYZ(1.0, 0.0, 0.0)  # размер идёт по X-протяжённости, смещение по Y
                tasks_horiz.append((elem, pt, mdir_h, wref_h, dist_h, aref, asrc, curve))
                placed += 1

            # Размер по X: до ближайшей ВЕРТИКАЛЬНОЙ стены (каскад влево)
            wi_v, wref_v, dist_v = find_wall_oriented(walls, pt, False, max_ft)
            if wi_v is not None:
                mdir_v = XYZ(0.0, 1.0, 0.0)
                tasks_vert.append((elem, pt, mdir_v, wref_v, dist_v, aref, asrc, curve))
                placed += 1

            if placed == 0:
                print("  [ПРОПУСК] ID={}: нет стен в радиусе".format(elem.Id))

        except Exception as ex:
            print("  [ОШИБКА] ID={}: {}".format(elem.Id, ex))

    tasks_horiz.sort(key=lambda t: t[1].X)
    tasks_vert.sort(key=lambda t: t[1].Y)

    # ── ФИЛЬТРАЦИЯ ────────────────────────────────────────────────────────────
    # 1) убрать размеры меньше MIN_DIM_MM
    # 2) убрать дубли по значению в пределах одного направления (± DEDUP_TOL_MM)
    def filter_tasks(tasks):
        min_ft = mm_to_ft(MIN_DIM_MM)
        tol_mm = DEDUP_TOL_MM
        out = []
        seen_mm = []   # уже использованные значения (в мм)
        skip_small = skip_dup = 0
        for t in tasks:
            dist = t[4]
            dmm  = dist * 304.8
            if dist < min_ft:
                skip_small += 1
                continue
            # дубль по значению?
            is_dup = False
            for v in seen_mm:
                if abs(v - dmm) <= tol_mm:
                    is_dup = True
                    break
            if is_dup:
                skip_dup += 1
                continue
            seen_mm.append(dmm)
            out.append(t)
        return out, skip_small, skip_dup

    tasks_horiz, h_small, h_dup = filter_tasks(tasks_horiz)
    tasks_vert,  v_small, v_dup = filter_tasks(tasks_vert)
    print("Фильтр: убрано мелких (<{}мм): {}, дублей: {}".format(
        MIN_DIM_MM, h_small + v_small, h_dup + v_dup))

    print("\nГоризонтальных: {}  Вертикальных: {}".format(
        len(tasks_horiz), len(tasks_vert)))

    if DRY_RUN:
        print("\n===== ДИАГНОСТИКА (DRY_RUN, размеры НЕ строятся) =====")
        print("Колонки: ID | ориент | источник_оси | дист_до_стены_мм | длина_короба_мм")
        for label, tlist in (("HORIZ", tasks_horiz), ("VERT", tasks_vert)):
            for task in tlist:
                elem, pt, mdir, wref, dist, aref, asrc, ecurve = task
                loc = elem.Location
                ln = 0.0
                if isinstance(loc, LocationCurve):
                    ln = loc.Curve.Length * 304.8
                flag = "" if asrc == "onAxis" else "  <-- ось НЕ на оси короба (проверь: {})".format(asrc)
                print("  {} ID={} src={} axisRef={} wallRef={} dist={:.0f} len={:.0f}{}".format(
                    label, elem.Id, asrc, ref_kind(aref), ref_kind(wref),
                    dist * 304.8, ln, flag))
        print("===== Поставь DRY_RUN=False чтобы строить размеры =====")
        return

    # ── Построение ОТДЕЛЬНЫХ размеров (вариант А) ─────────────────────────────
    # Каждый короб -> свой размер: грань стены + ось короба.
    # Линия размера параллельна стене, смещена перпендикулярно от короба.
    # Каскад: соседние размеры разносим по уровням, чтобы текст не накладывался.

    def stable(ref):
        try:
            return ref.ConvertToStableRepresentation(doc)
        except Exception:
            return None

    def build_single(tasks, horiz, dimtype):
        """Каждому коробу — отдельный размер. Каскад по уровням."""
        if not tasks:
            return 0, 0
        made = miss = 0
        # сортировка вдоль стены, чтобы каскад шёл по порядку
        if horiz:
            tasks = sorted(tasks, key=lambda t: t[1].X)
        else:
            tasks = sorted(tasks, key=lambda t: t[1].Y)

        for idx, task in enumerate(tasks):
            elem, pt, mdir, wref, dist, aref, asrc, ecurve = task
            try:
                # Если Reference оси штатно не достался — создаём линию детали
                # точно по оси короба в этом виде. У неё Reference есть всегда.
                if aref == "NEED_LINE":
                    aref = make_axis_line_ref(ecurve, pt, mdir)
                    if aref is None:
                        print("  [ПРОПУСК] ID={}: не удалось создать линию оси".format(elem.Id))
                        miss += 1; continue

                ra = ReferenceArray()
                ra.Append(wref)
                ra.Append(aref)
                if ra.Size < 2:
                    miss += 1; continue

                z = pt.Z
                # направление вдоль стены и перпендикуляр
                d = XYZ(mdir.X, mdir.Y, 0.0)
                if d.GetLength() < 1e-9:
                    d = XYZ(1.0, 0.0, 0.0) if horiz else XYZ(0.0, 1.0, 0.0)
                udir = d.Normalize()
                pdir = XYZ(-udir.Y, udir.X, 0.0)

                # проекция центра короба
                pu = pt.X * udir.X + pt.Y * udir.Y
                pp = pt.X * pdir.X + pt.Y * pdir.Y

                # смещение линии размера ставим ЗА стену (дальше короба в сторону стены),
                # каскад циклический по уровням
                lvl = idx % CASCADE_LEVELS
                off = dist + first_off + step * lvl
                base_p = pp - off

                half = dist + mm_to_ft(500)
                def to_xyz(u, pv):
                    return XYZ(udir.X * u + pdir.X * pv,
                               udir.Y * u + pdir.Y * pv, z)
                dl = Line.CreateBound(to_xyz(pu - half, base_p),
                                      to_xyz(pu + half, base_p))

                if dimtype is not None:
                    nd = doc.Create.NewDimension(view, dl, ra, dimtype)
                else:
                    nd = doc.Create.NewDimension(view, dl, ra)
                if nd:
                    print("  [OK {}] ID={} {:.0f}мм".format(
                        "↓" if horiz else "←", elem.Id, dist * 304.8))
                    made += 1
                else:
                    print("  [ПРОПУСК] ID={}: None".format(elem.Id))
                    miss += 1
            except Exception as ex:
                print("  [ОШИБКА] ID={}: {}".format(elem.Id, ex))
                miss += 1
        return made, miss

    created = skipped = 0
    dimtype = get_aligned_dimtype()

    with Transaction(doc, "AutoDim Conduit Single") as t:
        t.Start()
        c1, s1 = build_single(tasks_horiz, True, dimtype)
        c2, s2 = build_single(tasks_vert, False, dimtype)
        created = c1 + c2
        skipped = s1 + s2
        t.Commit()

    print("\n==============================")
    print("Размеров создано: {}".format(created))
    print("Пропущено: {}".format(skipped))
    print("==============================")

if __name__ == '__main__':
    main()