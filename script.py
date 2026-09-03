# -*- coding: utf-8 -*-

from Autodesk.Revit.DB import *
from System.Collections.Generic import List
from Autodesk.Revit.UI.Selection import ObjectType
from pyrevit import forms
import math

uidoc = __revit__.ActiveUIDocument
doc = uidoc.Document

# =========================================================
# ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ ГЕОМЕТРИИ
# =========================================================
def get_point(ref):
    try:
        if not ref: return None
        trans = Transform.Identity
        # Проверяем, выбран ли элемент из связанного файла
        if hasattr(ref, "LinkedElementId") and ref.LinkedElementId != ElementId.InvalidElementId:
            link_inst = doc.GetElement(ref.ElementId)
            trans = link_inst.GetTotalTransform() # Получаем трансформацию связи
            link_doc = link_inst.GetLinkDocument()
            el = link_doc.GetElement(ref.LinkedElementId)
        else:
            el = doc.GetElement(ref.ElementId)
            
        if not el: return None
        
        # Для оборудования берем центр верхней грани (X_center, Y_center, Z_max)
        bb = el.get_BoundingBox(None)
        if bb:
            c_x = (bb.Min.X + bb.Max.X) / 2.0
            c_y = (bb.Min.Y + bb.Max.Y) / 2.0
            z_top = bb.Max.Z # Строго верхняя грань
            local_top_center = XYZ(c_x, c_y, z_top)
            return trans.OfPoint(local_top_center) # Переводим в координаты главного файла
            
        # Резервный вариант, если BoundingBox недоступен
        loc = el.Location
        if isinstance(loc, LocationPoint): return trans.OfPoint(loc.Point)
        if isinstance(loc, LocationCurve): return trans.OfPoint(loc.Curve.Evaluate(0.5, True))
    except: 
        return None

def get_connectors(el):
    connectors = []
    try:
        if hasattr(el, "ConnectorManager"): conns = el.ConnectorManager.Connectors
        elif hasattr(el, "MEPModel") and el.MEPModel: conns = el.MEPModel.ConnectorManager.Connectors
        else: return connectors
        for c in conns: connectors.append(c)
    except: pass
    return connectors

def get_element_center(el):
    loc = el.Location
    if isinstance(loc, LocationPoint): return loc.Point
    if isinstance(loc, LocationCurve): return loc.Curve.Evaluate(0.5, True)
    bb = el.get_BoundingBox(None)
    if bb: return (bb.Min + bb.Max) * 0.5
    return None

def sort_elements_by_geometry(elements, start_pt):
    sorted_elements = []
    pool = list(elements)
    current_pos = start_pt
    while pool:
        best_el = None
        min_dist = float('inf')
        for el in pool:
            conns = get_connectors(el)
            d = min([c.Origin.DistanceTo(current_pos) for c in conns]) if conns else (get_element_center(el).DistanceTo(current_pos) if get_element_center(el) else float('inf'))
            if d < min_dist:
                min_dist = d
                best_el = el
        if best_el:
            sorted_elements.append(best_el)
            pool.remove(best_el)
            b_conns = get_connectors(best_el)
            current_pos = max(b_conns, key=lambda c: c.Origin.DistanceTo(current_pos)).Origin if b_conns else (get_element_center(best_el) or current_pos)
        else: break
    return sorted_elements

def get_continuous_centerline(elements, start_pt, end_pt):
    raw_curves = []
    current_look_at = start_pt
    for idx, el in enumerate(elements):
        try:
            loc = el.Location
            if isinstance(loc, LocationCurve):
                curve = loc.Curve
                conns = get_connectors(el)
                if curve and len(conns) >= 2:
                    p1, p2 = conns[0].Origin, conns[1].Origin
                    line = Line.CreateBound(p1, p2) if p1.DistanceTo(current_look_at) <= p2.DistanceTo(current_look_at) else Line.CreateBound(p2, p1)
                    raw_curves.append(line)
                    current_look_at = line.GetEndPoint(1)
                else:
                    if curve:
                        raw_curves.append(curve)
                        current_look_at = curve.GetEndPoint(1)
            else:
                conns = get_connectors(el)
                if not conns or len(conns) < 2: continue
                conns_pts = [c.Origin for c in conns]
                in_pt = min(conns_pts, key=lambda p: p.DistanceTo(current_look_at))
                out_pt_target = min([nc.Origin for nc in get_connectors(elements[idx + 1])], key=lambda p: p.DistanceTo(in_pt)) if idx < len(elements) - 1 and get_connectors(elements[idx + 1]) else end_pt
                remaining_pts = [p for p in conns_pts if p.DistanceTo(in_pt) > 0.001]
                if not remaining_pts: continue
                out_pt = min(remaining_pts, key=lambda p: p.DistanceTo(out_pt_target))
                
                corner1 = XYZ(in_pt.X, out_pt.Y, in_pt.Z)
                corner2 = XYZ(out_pt.X, in_pt.Y, in_pt.Z)
                inter_pt = corner1 if abs(corner1.DistanceTo(in_pt) - corner1.DistanceTo(out_pt)) <= abs(corner2.DistanceTo(in_pt) - corner2.DistanceTo(out_pt)) else corner2
                
                if in_pt.DistanceTo(inter_pt) > 0.01 and inter_pt.DistanceTo(out_pt) > 0.01:
                    try:
                        dir_in = (in_pt - inter_pt).Normalize()
                        dir_out = (out_pt - inter_pt).Normalize()
                        bisector = (dir_in + dir_out).Normalize()
                        
                        angle = dir_in.AngleTo(dir_out)
                        r = inter_pt.DistanceTo(in_pt)
                        
                        if angle > 0.01 and angle < math.pi - 0.01:
                            radius = r * math.tan(angle / 2.0)
                            dist_to_center = radius / math.sin(angle / 2.0)
                            
                            center_of_arc = inter_pt + bisector * dist_to_center
                            arc_mid = center_of_arc - bisector * radius

                            geom_arc = Arc.Create(in_pt, out_pt, arc_mid)
                            raw_curves.append(geom_arc)
                        else:
                            raw_curves.append(Line.CreateBound(in_pt, out_pt))
                    except:
                        raw_curves.append(Line.CreateBound(in_pt, out_pt))
                else:
                    raw_curves.append(Line.CreateBound(in_pt, out_pt))
                current_look_at = out_pt
        except: pass

    if not raw_curves: return []
    stitched = [raw_curves[0]]
    for i in range(1, len(raw_curves)):
        p_end = stitched[-1].GetEndPoint(1)
        n_start = raw_curves[i].GetEndPoint(0)
        if p_end.DistanceTo(n_start) > 0.001:
            try: stitched.append(Line.CreateBound(p_end, n_start))
            except: pass
        stitched.append(raw_curves[i])
    return stitched

def intersection_xy_pure(line1, line2):
    try:
        p1, p2 = line1.GetEndPoint(0), line1.GetEndPoint(1)
        p3, p4 = line2.GetEndPoint(0), line2.GetEndPoint(1)
        
        x1, y1 = p1.X, p1.Y
        x2, y2 = p2.X, p2.Y
        x3, y3 = p3.X, p3.Y
        x4, y4 = p4.X, p4.Y
        
        denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
        if abs(denom) < 1e-5:
            return None 
            
        num_x = (x1 * y2 - y1 * x2) * (x3 - x4) - (x1 - x2) * (x3 * y4 - y3 * x4)
        num_y = (x1 * y2 - y1 * x2) * (y3 - y4) - (y1 - y2) * (x3 * y4 - y3 * x4)
        
        return XYZ(num_x / denom, num_y / denom, (p2.Z + p3.Z) / 2.0)
    except:
        return None

# =========================================================
# АЛГОРИТМ АВТОМАТИЧЕСКОГО СКУСКА И ОБРЕЗКИ ХВОСТОВ (ОРТОГОНАЛЬНЫЙ)
# =========================================================
def trim_path_and_add_drops(curves, start_pt, end_pt):
    if not curves:
        # Если лотков нет, строим чистый Г-образный опус напрямую между точками
        if start_pt.DistanceTo(end_pt) > 0.001:
            turn = XYZ(end_pt.X, end_pt.Y, start_pt.Z)
            res = []
            if start_pt.DistanceTo(turn) > 0.001: res.append(Line.CreateBound(start_pt, turn))
            if turn.DistanceTo(end_pt) > 0.001: res.append(Line.CreateBound(turn, end_pt))
            return res
        return []
    
    # Ищем сегмент лотка, к которому ближе всего точка старта
    min_d_start = float('inf')
    idx_start = 0
    pt_on_path_start = curves[0].GetEndPoint(0)
    
    for i, c in enumerate(curves):
        try:
            proj = c.Project(start_pt)
            if proj and proj.Distance < min_d_start:
                min_d_start = proj.Distance
                idx_start = i
                pt_on_path_start = proj.XYZPoint
        except: pass
        
    # Ищем сегмент лотка, к которому ближе всего точка финиша
    min_d_end = float('inf')
    idx_end = len(curves) - 1
    pt_on_path_end = curves[-1].GetEndPoint(1)
    
    for i, c in enumerate(curves):
        try:
            proj = c.Project(end_pt)
            if proj and proj.Distance < min_d_end:
                min_d_end = proj.Distance
                idx_end = i
                pt_on_path_end = proj.XYZPoint
        except: pass

    # Сортируем индексы на случай, если порядок выбора элементов задом наперёд
    if idx_start > idx_end:
        idx_start, idx_end = idx_end, idx_start
        pt_on_path_start, pt_on_path_end = pt_on_path_end, pt_on_path_start

    trimmed_chain = []

    # Случай 1: Старт и финиш проецируются на один и тот же участок лотка
    if idx_start == idx_end:
        c = curves[idx_start]
        if pt_on_path_start.DistanceTo(pt_on_path_end) > 0.001:
            try:
                if isinstance(c, Line):
                    trimmed_chain.append(Line.CreateBound(pt_on_path_start, pt_on_path_end))
                elif isinstance(c, Arc):
                    p1 = c.Project(pt_on_path_start).Parameter
                    p2 = c.Project(pt_on_path_end).Parameter
                    trimmed_chain.append(Arc.Create(pt_on_path_start, pt_on_path_end, c.Evaluate((p1+p2)*0.5, False)))
                else:
                    trimmed_chain.append(Line.CreateBound(pt_on_path_start, pt_on_path_end))
            except:
                trimmed_chain.append(Line.CreateBound(pt_on_path_start, pt_on_path_end))
    else:
        # Случай 2: Проекции лежат на разных сегментах лотка
        # 1. Обрезаем первый сегмент от точки проекции до его конца
        c_first = curves[idx_start]
        ep1 = c_first.GetEndPoint(1)
        if pt_on_path_start.DistanceTo(ep1) > 0.001:
            try:
                if isinstance(c_first, Line):
                    trimmed_chain.append(Line.CreateBound(pt_on_path_start, ep1))
                elif isinstance(c_first, Arc):
                    p1 = c_first.Project(pt_on_path_start).Parameter
                    p2 = c_first.Project(ep1).Parameter
                    trimmed_chain.append(Arc.Create(pt_on_path_start, ep1, c_first.Evaluate((p1+p2)*0.5, False)))
                else:
                    trimmed_chain.append(Line.CreateBound(pt_on_path_start, ep1))
            except:
                trimmed_chain.append(Line.CreateBound(pt_on_path_start, ep1))

        # 2. Добавляем все промежуточные сегменты без изменений
        for i in range(idx_start + 1, idx_end):
            trimmed_chain.append(curves[i])

        # 3. Обрезаем последний сегмент от его начала до точки проекции финиша
        c_last = curves[idx_end]
        lp0 = c_last.GetEndPoint(0)
        if lp0.DistanceTo(pt_on_path_end) > 0.001:
            try:
                if isinstance(c_last, Line):
                    trimmed_chain.append(Line.CreateBound(lp0, pt_on_path_end))
                elif isinstance(c_last, Arc):
                    p1 = c_last.Project(lp0).Parameter
                    p2 = c_last.Project(pt_on_path_end).Parameter
                    trimmed_chain.append(Arc.Create(lp0, pt_on_path_end, c_last.Evaluate((p1+p2)*0.5, False)))
                else:
                    trimmed_chain.append(Line.CreateBound(lp0, pt_on_path_end))
            except:
                trimmed_chain.append(Line.CreateBound(lp0, pt_on_path_end))

    # Сшиваем финальную геометрию кабеля с ОРТОГОНАЛЬНЫМИ спусками
    result_curves = []
    if trimmed_chain:
        # --- ОРТОГОНАЛЬНЫЙ СТАРТ ---
        try:
            pt_on_tray_start = trimmed_chain[0].GetEndPoint(0)
            if start_pt.DistanceTo(pt_on_tray_start) > 0.001:
                # Точка излома: координаты шкафа, но высота лотка
                turn_start = XYZ(start_pt.X, start_pt.Y, pt_on_tray_start.Z)
                if start_pt.DistanceTo(turn_start) > 0.001:
                    result_curves.append(Line.CreateBound(start_pt, turn_start)) # Вертикально вверх от шкафа
                if turn_start.DistanceTo(pt_on_tray_start) > 0.001:
                    result_curves.append(Line.CreateBound(turn_start, pt_on_tray_start)) # Горизонтально до оси лотка
        except: pass
        
        result_curves.extend(trimmed_chain)
        
        # --- ОРТОГОНАЛЬНЫЙ ФИНИШ ---
        try:
            pt_on_tray_end = trimmed_chain[-1].GetEndPoint(1)
            if pt_on_tray_end.DistanceTo(end_pt) > 0.001:
                # Точка излома: координаты шкафа, но высота лотка
                turn_end = XYZ(end_pt.X, end_pt.Y, pt_on_tray_end.Z)
                if pt_on_tray_end.DistanceTo(turn_end) > 0.001:
                    result_curves.append(Line.CreateBound(pt_on_tray_end, turn_end)) # Горизонтально по лотку до оси шкафа
                if turn_end.DistanceTo(end_pt) > 0.001:
                    result_curves.append(Line.CreateBound(turn_end, end_pt)) # Вертикально вниз строго до верхней грани
        except: pass
    else:
        try:
            if start_pt.DistanceTo(end_pt) > 0.001:
                turn = XYZ(end_pt.X, end_pt.Y, start_pt.Z)
                if start_pt.DistanceTo(turn) > 0.001: result_curves.append(Line.CreateBound(start_pt, turn))
                if turn.DistanceTo(end_pt) > 0.001: result_curves.append(Line.CreateBound(turn, end_pt))
        except: pass
        
    return result_curves

# =========================================================
# ВВОД ДАННЫХ
# =========================================================
a_ref = uidoc.Selection.PickObject(ObjectType.LinkedElement, "Выберите ТОЧКУ НАЧАЛА трассы")
b_ref = uidoc.Selection.PickObject(ObjectType.LinkedElement, "Выберите ТОЧКУ КОНЦА трассы")
p_start, p_end = get_point(a_ref), get_point(b_ref)

if not p_start or not p_end:
    forms.alert("Не удалось определить координаты точек.", title="Ошибка")
    raise SystemExit

route_refs = uidoc.Selection.PickObjects(ObjectType.Element, "Выберите лотки и фитинги")
raw_elements = [doc.GetElement(r.ElementId) for r in route_refs if doc.GetElement(r.ElementId)]
route_elements = sort_elements_by_geometry(raw_elements, p_start)

c_level = forms.ask_for_string(title="Уровень кабеля", prompt="Введите назначение:", default="Силовой")
c_mark = forms.ask_for_string(title="Марка кабеля по схеме", prompt="Введите марку кабеля:", default="W1")
c_name = forms.ask_for_string(title="Наименование кабеля", prompt="Введите марку сечения кабеля:", default="ВВГнг(А)-FRLS 3х2.5")

c_row_str = forms.ask_for_string(title="Раскладка в лотке", prompt="Введите номер ряда:\n0 = Центр\n1 = Правее (Внешний ряд)\n-1 = Левее (Внутренний ряд)", default="0")
try: c_row = int(c_row_str)
except: c_row = 0

cable_size = 0.015  
step_offset = 0.050 
half_size = cable_size / 2.0
offset_distance = c_row * step_offset

# Получаем ось трассы лотка
raw_base_curves = get_continuous_centerline(route_elements, p_start, p_end)

# ТОЧНАЯ ФИЛЬТРАЦИЯ (25 мм): Защита коротких участков
base_curves = []
for c in raw_base_curves:
    if isinstance(c, Line) and c.Length < 0.082:
        continue
    base_curves.append(c)

if not base_curves and raw_base_curves:
    base_curves = raw_base_curves

# =========================================================
# СДВИГ КРИВЫХ (ОФСЕТ)
# =========================================================
moved_curves = []
if abs(offset_distance) > 0.001 and len(base_curves) > 0:
    for curve in base_curves:
        if curve.Length < 0.001: continue
        try:
            if isinstance(curve, Line):
                tan = curve.ComputeDerivatives(0.0, True).BasisX.Normalize()
                off_vec = tan.CrossProduct(XYZ.BasisZ).Normalize() * offset_distance
                moved_curves.append(Line.CreateBound(curve.GetEndPoint(0) + off_vec, curve.GetEndPoint(1) + off_vec))
                
            elif isinstance(curve, Arc):
                center = curve.Center
                tan_start = curve.ComputeDerivatives(0.0, True).BasisX.Normalize()
                tan_end = curve.ComputeDerivatives(1.0, True).BasisX.Normalize()
                
                off_start = tan_start.CrossProduct(XYZ.BasisZ).Normalize() * offset_distance
                off_end = tan_end.CrossProduct(XYZ.BasisZ).Normalize() * offset_distance
                
                new_start = curve.GetEndPoint(0) + off_start
                new_end = curve.GetEndPoint(1) + off_end
                new_radius = center.DistanceTo(new_start)
                
                if new_radius > 0.01:
                    orig_mid = curve.Evaluate(0.5, True)
                    dir_mid = (orig_mid - center).Normalize()
                    new_mid = center + dir_mid * new_radius
                    off_c = Arc.Create(new_start, new_end, new_mid)
                    if off_c: moved_curves.append(off_c)
            else:
                off_c = curve.CreateOffset(offset_distance, XYZ.BasisZ)
                if off_c: moved_curves.append(off_c)
        except: pass
else:
    moved_curves = base_curves

# =========================================================
# СШИВКА ПО ТОЧКАМ СТЫКОВКИ
# =========================================================
final_curves = []
if len(moved_curves) > 0:
    working_curves = list(moved_curves)
    corrected_curves = []
    
    i = 0
    while i < len(working_curves):
        if i == len(working_curves) - 1:
            corrected_curves.append(working_curves[i])
            break
            
        c_curr = working_curves[i]
        c_next = working_curves[i+1]
        
        if isinstance(c_curr, Line) and isinstance(c_next, Line):
            inter_pt = intersection_xy_pure(c_curr, c_next)
            
            if inter_pt:
                try:
                    p0_curr = c_curr.GetEndPoint(0)
                    p1_next = c_next.GetEndPoint(1)
                    
                    line_curr_trimmed = Line.CreateBound(p0_curr, inter_pt)
                    line_next_trimmed = Line.CreateBound(inter_pt, p1_next)
                    
                    if line_curr_trimmed.Length > 0.001 and line_next_trimmed.Length > 0.001:
                        corrected_curves.append(line_curr_trimmed)
                        working_curves[i+1] = line_next_trimmed
                        i += 1
                        continue
                except:
                    pass
        
        corrected_curves.append(c_curr)
        
        p_end_curr = c_curr.GetEndPoint(1)
        p_start_next = c_next.GetEndPoint(0)
        if p_end_curr.DistanceTo(p_start_next) > 0.001:
            try:
                bridge = Line.CreateBound(p_end_curr, p_start_next)
                if bridge.Length > 0.001:
                    corrected_curves.append(bridge)
            except:
                pass
            
        i += 1
        
    final_curves = corrected_curves
else:
    final_curves = base_curves

# ПРИМЕНЕНИЕ АВТОМАТИЧЕСКОГО ОРТОГОНАЛЬНОГО СПУСКА
final_curves = trim_path_and_add_drops(final_curves, p_start, p_end)

# Расчет длины
total_physical_length_feet = sum([c.Length for c in final_curves])
length_in_meters = total_physical_length_feet * 0.3048
final_specification_length = round(length_in_meters * 1.1, 2)

# =========================================================
# ГЕНЕРАЦИЯ ТЕЛА (SOLID) И DIRECTSHAPE
# =========================================================
solids = List[GeometryObject]()
for curve in final_curves:
    if curve.Length < 0.001: continue
    tess_pts = curve.Tessellate()
    for i in range(len(tess_pts) - 1):
        start, end = tess_pts[i], tess_pts[i+1]
        vec = end - start
        length = vec.GetLength()
        if length < 0.001: continue

        direction = vec.Normalize()
        up = XYZ.BasisZ
        
        if abs(direction.DotProduct(up)) > 0.999:
            right = XYZ.BasisX
        else:
            right = direction.CrossProduct(up).Normalize()
        up = right.CrossProduct(direction).Normalize()

        p1 = start + right * half_size + up * half_size
        p2 = start - right * half_size + up * half_size
        p3 = start - right * half_size - up * half_size
        p4 = start + right * half_size - up * half_size

        profile = List[Curve]()
        profile.Add(Line.CreateBound(p1, p2))
        profile.Add(Line.CreateBound(p2, p3))
        profile.Add(Line.CreateBound(p3, p4))
        profile.Add(Line.CreateBound(p4, p1))

        try:
            profile_loop = CurveLoop.Create(profile)
            loops = List[CurveLoop]()
            loops.Add(profile_loop)
            solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, length)
            if solid: solids.Add(solid)
        except: pass

if solids.Count == 0:
    forms.alert("Не удалось сгенерировать 3D-объем.", title="Ошибка")
    raise SystemExit

# =========================================================
# СОХРАНЕНИЕ В REVIT И ЗАПОЛНЕНИЕ ПАРАМЕТРОВ
# =========================================================
def set_param(element, param_name, value, is_number=False):
    p = element.LookupParameter(param_name)
    if not p and param_name == "Mark" or param_name == "Марка":
        p = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
    if p and not p.IsReadOnly:
        try: p.Set(float(value)) if is_number else p.Set(str(value))
        except: pass

t = Transaction(doc, "Кабель - Автоопуск к оборудованию")
t.Start()
ds = DirectShape.CreateElement(doc, ElementId(BuiltInCategory.OST_GenericModel))
ds.ApplicationId = "pyRevitCableSpread"
ds.ApplicationDataId = c_mark
ds.SetShape(solids)

# Заполняем параметры
set_param(ds, "Комментарии", c_level)
set_param(ds, "ADSK_Марка", c_mark)
set_param(ds, "Марка", "Кабель")
set_param(ds, "ADSK_Наименование", c_name)
set_param(ds, "ADSK_Количество", final_specification_length, is_number=True)
t.Commit()

forms.alert("Готово!", title="Успешно")