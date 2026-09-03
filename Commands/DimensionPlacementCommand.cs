using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using GMDC.UI;

namespace GMDC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class DimensionPlacementCommand : IExternalCommand
    {
        public class WallInfo
        {
            public Wall Wall { get; set; } = null!;
            public Transform Transform { get; set; } = null!;
            public RevitLinkInstance? LinkInstance { get; set; }
            public Document? LinkDocument { get; set; }
        }

        public class DimTask
        {
            public Element Element { get; set; } = null!;
            public XYZ Midpoint { get; set; } = null!;
            public XYZ Direction { get; set; } = null!;
            public Reference WallRef { get; set; } = null!;
            public double DistanceFt { get; set; }
            public Reference AxisRef { get; set; } = null!;
            public string AxisSource { get; set; } = string.Empty;
            public Curve Curve { get; set; } = null!;
            public XYZ WallProjPoint { get; set; } = null!;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // Ensure we are in a plan view
            if (activeView.ViewType != ViewType.FloorPlan &&
                activeView.ViewType != ViewType.CeilingPlan &&
                activeView.ViewType != ViewType.AreaPlan &&
                activeView.ViewType != ViewType.EngineeringPlan)
            {
                TaskDialog.Show("GMDC AutoDim", "Пожалуйста, откройте план этажа и запустите команду снова.");
                return Result.Cancelled;
            }

            try
            {
                // Gather loaded Dimension Types for style selection
                List<DimensionType> dimStyles = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .Where(dt => dt.StyleType == DimensionStyleType.Linear)
                    .OrderBy(dt => dt.Name)
                    .ToList();

                if (!dimStyles.Any())
                {
                    TaskDialog.Show("GMDC AutoDim", "В проекте не найдено линейных стилей размеров.");
                    return Result.Cancelled;
                }

                // Check selection for MEP elements
                var selIds = uidoc.Selection.GetElementIds();
                var selectedMep = selIds
                    .Select(id => doc.GetElement(id))
                    .Where(IsMepElement)
                    .ToList();

                bool hasSelection = selectedMep.Any();

                // Open WPF settings dialog
                var window = new DimensionPlacementWindow(doc, dimStyles, hasSelection);
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (window.ShowDialog() != true || !window.IsPlaceRequested || window.SelectedStyle == null)
                {
                    return Result.Cancelled;
                }

                // Gather target MEP elements based on scope and category selections
                List<Element> targetMep;
                Func<Element, bool> categoryFilter = el =>
                {
                    if (el.Category == null) return false;
                    long catId = el.Category.Id.Value;

                    if (catId == (long)BuiltInCategory.OST_Conduit) return window.CatConduits;
                    if (catId == (long)BuiltInCategory.OST_CableTray) return window.CatCableTrays;
                    if (catId == (long)BuiltInCategory.OST_PipeCurves) return window.CatPipes;
                    if (catId == (long)BuiltInCategory.OST_DuctCurves) return window.CatDucts;

                    return false;
                };

                switch (window.SelectedScope)
                {
                    case "Selected":
                        targetMep = selectedMep.Where(categoryFilter).ToList();
                        break;
                    case "View":
                        targetMep = new FilteredElementCollector(doc, activeView.Id)
                            .WherePasses(GetMepFilter(window))
                            .WhereElementIsNotElementType()
                            .Cast<Element>()
                            .ToList();
                        break;
                    case "Project":
                        targetMep = new FilteredElementCollector(doc)
                            .WherePasses(GetMepFilter(window))
                            .WhereElementIsNotElementType()
                            .Cast<Element>()
                            .ToList();
                        break;
                    default:
                        targetMep = selectedMep.Where(categoryFilter).ToList();
                        break;
                }

                if (!targetMep.Any())
                {
                    TaskDialog.Show("GMDC AutoDim", "Не найдено элементов выбранных категорий для расстановки размеров.");
                    return Result.Cancelled;
                }

                // Collect walls (host and links)
                List<WallInfo> walls = CollectWalls(doc);
                if (!walls.Any())
                {
                    TaskDialog.Show("GMDC AutoDim", "В проекте и связях не найдено стен для образмеривания.");
                    return Result.Cancelled;
                }

                // Get IDs of already dimensioned elements in this view to avoid duplicates
                HashSet<long> alreadyDimensioned = GetExistingDimensionedElementIds(doc, activeView);

                double maxDistFt = window.MaxDistance / 304.8;
                double minDimFt = window.MinDim / 304.8;
                double firstOffsetFt = window.FirstOffset / 304.8;
                double cascadeStepFt = window.CascadeStep / 304.8;
                double dedupTolFt = window.DedupTol / 304.8;
                double minAxisLenFt = 100.0 / 304.8;
                double onAxisTolFt = 150.0 / 304.8;

                List<DimTask> tasksHoriz = new List<DimTask>();
                List<DimTask> tasksVert = new List<DimTask>();
                int skippedAlreadyDim = 0;
                int skippedNoAxis = 0;
                int skippedNoWalls = 0;

                foreach (Element elem in targetMep)
                {
                    if (alreadyDimensioned.Contains(elem.Id.Value))
                    {
                        skippedAlreadyDim++;
                        continue;
                    }

                    if (elem.Location is not LocationCurve locCurve || locCurve.Curve == null)
                    {
                        continue;
                    }

                    Curve curve = locCurve.Curve;
                    XYZ pt = curve.Evaluate(0.5, true);
                    XYZ rawDir = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                    XYZ mdir = rawDir.GetLength() > 1e-9 ? new XYZ(rawDir.X, rawDir.Y, 0.0).Normalize() : XYZ.BasisX;

                    var (aref, asrc) = GetAxisRef(elem, curve, activeView, minAxisLenFt, onAxisTolFt);
                    // Special marker "NEED_LINE" is processed inside transaction
                    if (aref == null && asrc != "needLine")
                    {
                        skippedNoAxis++;
                        continue;
                    }

                    // Find nearest horizontal wall (dimension in Y direction)
                    var (wallH, wrefH, distH, projPtH) = FindWallOriented(walls, pt, true, maxDistFt);
                    if (wallH != null && wrefH != null && projPtH != null)
                    {
                        tasksHoriz.Add(new DimTask
                        {
                            Element = elem,
                            Midpoint = pt,
                            Direction = mdir,
                            WallRef = wrefH,
                            DistanceFt = distH,
                            AxisRef = aref ?? new Reference(elem), // will be replaced if NEED_LINE
                            AxisSource = asrc,
                            Curve = curve,
                            WallProjPoint = projPtH
                        });
                    }

                    // Find nearest vertical wall (dimension in X direction)
                    var (wallV, wrefV, distV, projPtV) = FindWallOriented(walls, pt, false, maxDistFt);
                    if (wallV != null && wrefV != null && projPtV != null)
                    {
                        tasksVert.Add(new DimTask
                        {
                            Element = elem,
                            Midpoint = pt,
                            Direction = mdir,
                            WallRef = wrefV,
                            DistanceFt = distV,
                            AxisRef = aref ?? new Reference(elem),
                            AxisSource = asrc,
                            Curve = curve,
                            WallProjPoint = projPtV
                        });
                    }

                    if (wallH == null && wallV == null)
                    {
                        skippedNoWalls++;
                    }
                }

                // Filter tasks: remove small dimensions and filter duplicates per wall
                tasksHoriz = FilterTasks(tasksHoriz, minDimFt, dedupTolFt);
                tasksVert = FilterTasks(tasksVert, minDimFt, dedupTolFt);

                if (!tasksHoriz.Any() && !tasksVert.Any())
                {
                    TaskDialog.Show("GMDC AutoDim", $"Не найдено подходящих размеров для создания.\n(Пропущено уже образмеренных: {skippedAlreadyDim}, без осей: {skippedNoAxis}, нет стен рядом: {skippedNoWalls})");
                    return Result.Succeeded;
                }

                int createdCount = 0;
                int failedCount = 0;

                using (Transaction trans = new Transaction(doc, "GMDC - Авторазмеры"))
                {
                    trans.Start();

                    createdCount += BuildDimensions(doc, activeView, tasksHoriz, true, window.SelectedStyle, firstOffsetFt, cascadeStepFt, window.CascadeLevels, ref failedCount);
                    createdCount += BuildDimensions(doc, activeView, tasksVert, false, window.SelectedStyle, firstOffsetFt, cascadeStepFt, window.CascadeLevels, ref failedCount);

                    trans.Commit();
                }

                TaskDialog.Show("GMDC - Авторазмеры", 
                    $"Успешно создано размеров: {createdCount}\n" +
                    $"Не удалось создать: {failedCount}\n\n" +
                    $"Пропущено уже образмеренных: {skippedAlreadyDim}\n" +
                    $"Не найдено осей в геометрии: {skippedNoAxis}\n" +
                    $"Стены не найдены в радиусе {window.MaxDistance} мм: {skippedNoWalls}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool IsMepElement(Element el)
        {
            if (el.Category == null) return false;
            long catId = el.Category.Id.Value;
            return catId == (long)BuiltInCategory.OST_Conduit ||
                   catId == (long)BuiltInCategory.OST_CableTray ||
                   catId == (long)BuiltInCategory.OST_PipeCurves ||
                   catId == (long)BuiltInCategory.OST_DuctCurves;
        }

        private static ElementFilter GetMepFilter(DimensionPlacementWindow w)
        {
            List<ElementFilter> filters = new List<ElementFilter>();
            if (w.CatConduits) filters.Add(new ElementCategoryFilter(BuiltInCategory.OST_Conduit));
            if (w.CatCableTrays) filters.Add(new ElementCategoryFilter(BuiltInCategory.OST_CableTray));
            if (w.CatPipes) filters.Add(new ElementCategoryFilter(BuiltInCategory.OST_PipeCurves));
            if (w.CatDucts) filters.Add(new ElementCategoryFilter(BuiltInCategory.OST_DuctCurves));

            return new LogicalOrFilter(filters);
        }

        private static List<WallInfo> CollectWalls(Document doc)
        {
            List<WallInfo> list = new List<WallInfo>();

            // Host walls
            var hostWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>();

            foreach (var w in hostWalls)
            {
                list.Add(new WallInfo { Wall = w, Transform = Transform.Identity });
            }

            // Linked walls
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>();

            foreach (var link in links)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Transform xf = link.GetTotalTransform();
                var linkedWalls = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>();

                foreach (var w in linkedWalls)
                {
                    list.Add(new WallInfo
                    {
                        Wall = w,
                        Transform = xf,
                        LinkInstance = link,
                        LinkDocument = linkDoc
                    });
                }
            }

            return list;
        }

        private static HashSet<long> GetExistingDimensionedElementIds(Document doc, View view)
        {
            HashSet<long> set = new HashSet<long>();
            var dims = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>();

            foreach (var dim in dims)
            {
                try
                {
                    ReferenceArray refs = dim.References;
                    if (refs == null) continue;
                    foreach (Reference r in refs)
                    {
                        ElementId id = r.ElementId;
                        if (id != null && id != ElementId.InvalidElementId)
                        {
                            set.Add(id.Value);
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        private static bool AreParallel(XYZ v1, XYZ v2, double tol = 0.10)
        {
            return Math.Abs(v1.DotProduct(v2)) > (1.0 - tol);
        }

        private static double PtToLineDist(XYZ p, XYZ a, XYZ b)
        {
            XYZ ab = new XYZ(b.X - a.X, b.Y - a.Y, 0.0);
            double L = ab.GetLength();
            if (L < 1e-9)
            {
                return new XYZ(p.X - a.X, p.Y - a.Y, 0.0).GetLength();
            }
            XYZ abNorm = ab.Normalize();
            XYZ ap = new XYZ(p.X - a.X, p.Y - a.Y, 0.0);
            double proj = ap.DotProduct(abNorm);
            XYZ foot = new XYZ(a.X + abNorm.X * proj, a.Y + abNorm.Y * proj, 0.0);
            return new XYZ(p.X - foot.X, p.Y - foot.Y, 0.0).GetLength();
        }

        private static XYZ? GetWallDir(WallInfo wi)
        {
            if (wi.Wall.Location is not LocationCurve loc) return null;
            Curve c = loc.Curve;
            XYZ p0 = wi.Transform.OfPoint(c.GetEndPoint(0));
            XYZ p1 = wi.Transform.OfPoint(c.GetEndPoint(1));
            XYZ d = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0);
            return d.GetLength() > 1e-9 ? d.Normalize() : null;
        }

        private static (Reference? Ref, double Distance, XYZ? ProjPoint) GetWallFace(WallInfo wi, XYZ ptWorld)
        {
            Wall wall = wi.Wall;
            XYZ ptLocal = wi.LinkInstance != null ? wi.Transform.Inverse.OfPoint(ptWorld) : ptWorld;
            Reference? bestRef = null;
            double bestDist = double.PositiveInfinity;
            XYZ? bestProjPt = null;

            ShellLayerType[] shells = { ShellLayerType.Exterior, ShellLayerType.Interior };
            foreach (var shell in shells)
            {
                try
                {
                    var faces = HostObjectUtils.GetSideFaces(wall, shell);
                    foreach (Reference fref in faces)
                    {
                        Face? face = wall.GetGeometryObjectFromReference(fref) as Face;
                        if (face == null) continue;
                        IntersectionResult proj = face.Project(ptLocal);
                        if (proj == null) continue;
                        double d = proj.Distance;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestProjPt = wi.LinkInstance != null ? wi.Transform.OfPoint(proj.XYZPoint) : proj.XYZPoint;
                            bestRef = wi.LinkInstance != null ? fref.CreateLinkReference(wi.LinkInstance) : fref;
                        }
                    }
                }
                catch { }
            }
            return (bestRef, bestDist, bestProjPt);
        }

        private static (WallInfo? Wall, Reference? FaceRef, double Distance, XYZ? ProjPoint) FindWallOriented(
            List<WallInfo> walls, XYZ pt, bool wantHorizontal, double maxFt)
        {
            WallInfo? bestWi = null;
            Reference? bestRef = null;
            double bestDist = double.PositiveInfinity;
            XYZ? bestProjPt = null;

            foreach (var wi in walls)
            {
                XYZ? wd = GetWallDir(wi);
                if (wd == null) continue;
                bool wallIsHorizontal = Math.Abs(wd.X) >= 0.7;
                if (wallIsHorizontal != wantHorizontal) continue;

                var (fref, dist, projPt) = GetWallFace(wi, pt);
                if (fref == null || dist > maxFt) continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestWi = wi;
                    bestRef = fref;
                    bestProjPt = projPt;
                }
            }
            return (bestWi, bestRef, bestDist, bestProjPt);
        }

        private static (Reference? Ref, string Source) GetAxisRef(Element elem, Curve curve, View view, double minLenFt, double onAxisTolFt)
        {
            // 1. Try location curve directly
            Reference? locRef = GetLocationCurveReference(elem);
            if (locRef != null) return (locRef, "LocCurve");

            // Check if standing riser
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            XYZ raw = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0.0);
            if (raw.GetLength() < 1e-9)
            {
                return (null, "needLinePoint");
            }
            XYZ cdir = raw.Normalize();
            XYZ mid = new XYZ((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5, 0.0);

            Options opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
            GeometryElement? geom = null;
            try
            {
                geom = elem.get_Geometry(opt);
            }
            catch { }

            Reference? bestOnAxisRef = null;
            double bestOnAxisLen = -1.0;
            Reference? bestAnyRef = null;
            double bestAnyLen = -1.0;

            if (geom != null)
            {
                Stack<(GeometryObject, Transform)> stack = new Stack<(GeometryObject, Transform)>();
                foreach (var obj in geom)
                {
                    stack.Push((obj, Transform.Identity));
                }

                while (stack.Count > 0)
                {
                    var (obj, xf) = stack.Pop();
                    if (obj is GeometryInstance gi)
                    {
                        try
                        {
                            GeometryElement innerGeom = gi.GetInstanceGeometry();
                            if (innerGeom != null)
                            {
                                foreach (var io in innerGeom)
                                {
                                    stack.Push((io, xf));
                                }
                            }
                        }
                        catch { }
                    }
                    else if (obj is Line line)
                    {
                        if (line.Reference == null) continue;

                        try
                        {
                            XYZ q0 = xf.OfPoint(line.GetEndPoint(0));
                            XYZ q1 = xf.OfPoint(line.GetEndPoint(1));
                            XYZ d = new XYZ(q1.X - q0.X, q1.Y - q0.Y, 0.0);
                            double len = d.GetLength();

                            if (len > bestAnyLen)
                            {
                                bestAnyRef = line.Reference;
                                bestAnyLen = len;
                            }

                            if (len < minLenFt) continue;
                            XYZ ldir = d.Normalize();
                            if (!AreParallel(cdir, ldir)) continue;

                            if (PtToLineDist(mid, q0, q1) <= onAxisTolFt)
                            {
                                if (len > bestOnAxisLen)
                                {
                                    bestOnAxisRef = line.Reference;
                                    bestOnAxisLen = len;
                                }
                            }
                        }
                        catch
                        {
                            if (bestAnyLen < 0)
                            {
                                bestAnyRef = line.Reference;
                                bestAnyLen = 0.0;
                            }
                        }
                    }
                }
            }

            if (bestOnAxisRef != null) return (bestOnAxisRef, "onAxis");
            if (bestAnyRef != null) return (bestAnyRef, "geomAny");

            try
            {
                Reference r1 = curve.GetEndPointReference(1);
                if (r1 != null) return (r1, "endRef");
            }
            catch { }

            return (null, "needLine");
        }

        private static Reference? GetLocationCurveReference(Element elem)
        {
            try
            {
                if (elem.Location is LocationCurve lc && lc.Curve != null)
                {
                    if (lc.Curve.Reference != null) return lc.Curve.Reference;
                    for (int i = 0; i <= 1; i++)
                    {
                        Reference r = lc.Curve.GetEndPointReference(i);
                        if (r != null) return r;
                    }
                }
            }
            catch { }
            return null;
        }

        private static Reference? MakeAxisLineRef(Document doc, View activeView, Curve ecurve, XYZ pt, XYZ mdir, double minAxisLenFt)
        {
            try
            {
                double z = pt.Z;
                XYZ p0 = ecurve.GetEndPoint(0);
                XYZ p1 = ecurve.GetEndPoint(1);
                XYZ a = new XYZ(p0.X, p0.Y, z);
                XYZ b = new XYZ(p1.X, p1.Y, z);

                if (a.DistanceTo(b) < 10.0 / 304.8) // 10 mm
                {
                    XYZ u = new XYZ(mdir.X, mdir.Y, 0.0);
                    if (u.GetLength() < 1e-9) u = XYZ.BasisX;
                    u = u.Normalize();
                    double h = 100.0 / 304.8; // 100 mm in feet
                    a = new XYZ(pt.X - u.X * h, pt.Y - u.Y * h, z);
                    b = new XYZ(pt.X + u.X * h, pt.Y + u.Y * h, z);
                }

                Line line = Line.CreateBound(a, b);
                DetailCurve dl = doc.Create.NewDetailCurve(activeView, line);
                if (dl == null) return null;
                Curve geo = dl.GeometryCurve;
                if (geo != null && geo.Reference != null) return geo.Reference;

                try
                {
                    Reference r = geo.GetEndPointReference(0);
                    if (r != null) return r;
                }
                catch { }
            }
            catch { }
            return null;
        }

        private static List<DimTask> FilterTasks(List<DimTask> tasks, double minDimFt, double dedupTolFt)
        {
            List<DimTask> result = new List<DimTask>();

            // Group tasks by Wall Face Reference stability to deduplicate per wall face
            var groups = tasks.GroupBy(t => t.WallRef.ConvertToStableRepresentation(t.Element.Document));

            foreach (var group in groups)
            {
                List<DimTask> sortedGroup = group.OrderBy(t => t.DistanceFt).ToList();
                List<double> placedDistances = new List<double>();

                foreach (var task in sortedGroup)
                {
                    if (task.DistanceFt < minDimFt) continue;

                    // Check for duplicate distances on the same wall
                    bool isDuplicate = false;
                    foreach (double dist in placedDistances)
                    {
                        if (Math.Abs(dist - task.DistanceFt) <= dedupTolFt)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

                    if (!isDuplicate)
                    {
                        placedDistances.Add(task.DistanceFt);
                        result.Add(task);
                    }
                }
            }

            return result;
        }

        private static int BuildDimensions(
            Document doc,
            View view,
            List<DimTask> tasks,
            bool horiz,
            DimensionType dimType,
            double firstOffsetFt,
            double cascadeStepFt,
            int cascadeLevels,
            ref int failedCount)
        {
            if (!tasks.Any()) return 0;

            int made = 0;

            // Group tasks by coplanar/collinear wall faces (using a 1000mm tolerance)
            List<List<DimTask>> groups = new List<List<DimTask>>();
            double toleranceFt = 1000.0 / 304.8; // 1000 mm

            foreach (var task in tasks)
            {
                List<DimTask>? foundGroup = null;
                foreach (var g in groups)
                {
                    DimTask representative = g[0];

                    if (horiz)
                    {
                        // Measuring Y-distance. Wall runs horizontally.
                        // Group if they are on the same side (top vs bottom) and have very close Y-coordinates.
                        bool repTop = representative.WallProjPoint.Y > representative.Midpoint.Y;
                        bool taskTop = task.WallProjPoint.Y > task.Midpoint.Y;
                        if (repTop == taskTop)
                        {
                            if (Math.Abs(representative.WallProjPoint.Y - task.WallProjPoint.Y) < toleranceFt)
                            {
                                foundGroup = g;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Measuring X-distance. Wall runs vertically.
                        // Group if they are on the same side (right vs left) and have very close X-coordinates.
                        bool repRight = representative.WallProjPoint.X > representative.Midpoint.X;
                        bool taskRight = task.WallProjPoint.X > task.Midpoint.X;
                        if (repRight == taskRight)
                        {
                            if (Math.Abs(representative.WallProjPoint.X - task.WallProjPoint.X) < toleranceFt)
                            {
                                foundGroup = g;
                                break;
                            }
                        }
                    }
                }

                if (foundGroup != null)
                {
                    foundGroup.Add(task);
                }
                else
                {
                    groups.Add(new List<DimTask> { task });
                }
            }

            foreach (var group in groups)
            {
                // Sort tasks in this group by actual distance to the wall (shortest to longest)
                List<DimTask> sortedTasks = group.OrderBy(t => t.DistanceFt).ToList();

                // Compute a single base coordinate for the entire group to ensure uniform offsets
                double baseCoord = 0;
                bool groupPositiveDir = false;

                if (horiz)
                {
                    // Vertical dimensions (measuring Y-distance, wall is horizontal)
                    // We cascade along X (left or right of the building)
                    groupPositiveDir = sortedTasks.Average(t => t.WallProjPoint.X - t.Midpoint.X) > 0;
                    if (groupPositiveDir)
                    {
                        baseCoord = sortedTasks.Max(t => t.WallProjPoint.X);
                    }
                    else
                    {
                        baseCoord = sortedTasks.Min(t => t.WallProjPoint.X);
                    }
                }
                else
                {
                    // Horizontal dimensions (measuring X-distance, wall is vertical)
                    // We cascade along Y (top or bottom of the building)
                    groupPositiveDir = sortedTasks.Average(t => t.WallProjPoint.Y - t.Midpoint.Y) > 0;
                    if (groupPositiveDir)
                    {
                        baseCoord = sortedTasks.Max(t => t.WallProjPoint.Y);
                    }
                    else
                    {
                        baseCoord = sortedTasks.Min(t => t.WallProjPoint.Y);
                    }
                }

                for (int idx = 0; idx < sortedTasks.Count; idx++)
                {
                    DimTask task = sortedTasks[idx];
                    try
                    {
                        Reference? aref = task.AxisRef;
                        if (task.AxisSource == "needLine" || task.AxisSource == "needLinePoint")
                        {
                            aref = MakeAxisLineRef(doc, view, task.Curve, task.Midpoint, task.Direction, 100.0 / 304.8);
                            if (aref == null)
                            {
                                failedCount++;
                                continue;
                            }
                        }

                        ReferenceArray ra = new ReferenceArray();
                        ra.Append(task.WallRef);
                        ra.Append(aref);

                        if (ra.Size < 2)
                        {
                            failedCount++;
                            continue;
                        }

                        double z = task.Midpoint.Z;

                        // Cascade calculation
                        int lvl = idx % cascadeLevels;
                        double off = firstOffsetFt + (cascadeStepFt * lvl);

                        // Uniform coordinate along the cascade axis
                        double dimCoord = groupPositiveDir ? baseCoord + off : baseCoord - off;

                        double halfLen = task.DistanceFt + (500.0 / 304.8); // padding of 500mm
                        Line dimLine;
                        if (horiz)
                        {
                            // Vertical dimension (measuring Y-distance) -> VERTICAL line
                            double pu = task.Midpoint.Y; // center along Y
                            dimLine = Line.CreateBound(
                                new XYZ(dimCoord, pu - halfLen, z),
                                new XYZ(dimCoord, pu + halfLen, z)
                            );
                        }
                        else
                        {
                            // Horizontal dimension (measuring X-distance) -> HORIZONTAL line
                            double pu = task.Midpoint.X; // center along X
                            dimLine = Line.CreateBound(
                                new XYZ(pu - halfLen, dimCoord, z),
                                new XYZ(pu + halfLen, dimCoord, z)
                            );
                        }

                        Dimension nd = doc.Create.NewDimension(view, dimLine, ra, dimType);
                        if (nd != null)
                        {
                            made++;
                        }
                        else
                        {
                            failedCount++;
                        }
                    }
                    catch
                    {
                        failedCount++;
                    }
                }
            }

            return made;
        }
    }
}
