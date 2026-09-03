using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using GMDC.UI;

namespace GMDC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class WireTrayHangerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Gather loaded Generic Model symbols
                List<FamilySymbol> symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .Cast<FamilySymbol>()
                    .ToList();

                // Check selection for CableTrayFitting elements
                var selIds = uidoc.Selection.GetElementIds();
                var selectedFittings = selIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el is FamilyInstance fi && fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting)
                    .Cast<FamilyInstance>()
                    .ToList();

                bool hasSelection = selectedFittings.Any();

                // Open WPF dialog
                var window = new WireTrayHangerWindow(doc, symbols, hasSelection);
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (window.ShowDialog() != true || !window.IsPlaceRequested)
                {
                    return Result.Cancelled;
                }

                // Gather target fittings based on scope and filter
                List<FamilyInstance> targetFittings;
                string filterStr = window.FittingFilter;

                Func<FamilyInstance, bool> matchesFilter = fi =>
                {
                    if (fi.Symbol == null || fi.Symbol.Family == null) return false;
                    string familyName = fi.Symbol.Family.Name;
                    return familyName.IndexOf(filterStr, StringComparison.OrdinalIgnoreCase) >= 0;
                };

                switch (window.SelectedScope)
                {
                    case "Selected":
                        targetFittings = selectedFittings.Where(matchesFilter).ToList();
                        break;
                    case "View":
                        targetFittings = new FilteredElementCollector(doc, doc.ActiveView.Id)
                            .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                            .WhereElementIsNotElementType()
                            .Cast<FamilyInstance>()
                            .Where(matchesFilter)
                            .ToList();
                        break;
                    case "Project":
                        targetFittings = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                            .WhereElementIsNotElementType()
                            .Cast<FamilyInstance>()
                            .Where(matchesFilter)
                            .ToList();
                        break;
                    default:
                        targetFittings = selectedFittings.Where(matchesFilter).ToList();
                        break;
                }

                if (!targetFittings.Any())
                {
                    TaskDialog.Show("GMDC", $"Не найдено соединительных деталей лотков, соответствующих фильтру семейства: \"{filterStr}\"");
                    return Result.Cancelled;
                }

                // Counters for skip reasons
                var skipReasons = new Dictionary<string, int>
                {
                    { "not_horizontal", 0 },
                    { "no_width_param", 0 },
                    { "no_family", 0 },
                    { "no_location", 0 },
                    { "no_floor_above", 0 },
                    { "no_level", 0 },
                    { "no_length", 0 }
                };

                int placedCount = 0;
                int processedCount = 0;

                using (var trans = new Transaction(doc, "GMDC - Крепеж проволочного лотка"))
                {
                    trans.Start();

                    foreach (var fitting in targetFittings)
                    {
                        // 1. Check if horizontal (Z-axis must be close to vertical)
                        Transform transform = fitting.GetTransform();
                        XYZ basisZ = transform.BasisZ;
                        double dot = Math.Abs(basisZ.DotProduct(XYZ.BasisZ));
                        double angleRad = Math.Acos(Math.Min(1.0, dot));
                        double angleDeg = angleRad * (180.0 / Math.PI);
                        if (angleDeg > 10.0)
                        {
                            skipReasons["not_horizontal"]++;
                            continue;
                        }

                        // 2. Get Width from parameter "И_Э_ШиринаЛотка" or fallback to standard
                        double widthMm = 0;
                        Parameter widthParam = fitting.LookupParameter("И_Э_ШиринаЛотка");
                        if (widthParam != null && widthParam.HasValue)
                        {
                            widthMm = Math.Round(widthParam.AsDouble() * 304.8, 1);
                        }
                        else
                        {
                            widthParam = fitting.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                            if (widthParam != null && widthParam.HasValue)
                            {
                                widthMm = Math.Round(widthParam.AsDouble() * 304.8, 1);
                            }
                        }

                        if (widthMm <= 0)
                        {
                            skipReasons["no_width_param"]++;
                            continue;
                        }

                        // 3. Determine Family Symbol to place
                        FamilySymbol? symbolToPlace = null;
                        if (window.IsAutoFamily)
                        {
                            string targetSymbolName = $"Узел крепления проволочный {widthMm:F0}";
                            symbolToPlace = symbols.FirstOrDefault(s => 
                                s.Name.IndexOf(targetSymbolName, StringComparison.OrdinalIgnoreCase) >= 0 || 
                                (s.Family != null && s.Family.Name.IndexOf(targetSymbolName, StringComparison.OrdinalIgnoreCase) >= 0)
                            );

                            if (symbolToPlace == null)
                            {
                                // Secondary attempt: search for a symbol containing the width value and "проволочный"
                                symbolToPlace = symbols.FirstOrDefault(s => 
                                    s.Name.Contains(widthMm.ToString("F0")) && 
                                    (s.Name.IndexOf("проволоч", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                     (s.Family != null && s.Family.Name.IndexOf("проволоч", StringComparison.OrdinalIgnoreCase) >= 0))
                                );
                            }
                        }
                        else
                        {
                            symbolToPlace = window.SelectedSymbol;
                        }

                        if (symbolToPlace == null)
                        {
                            skipReasons["no_family"]++;
                            continue;
                        }

                        // Activate the family symbol if not active
                        if (!symbolToPlace.IsActive)
                        {
                            symbolToPlace.Activate();
                        }

                        // 4. Get Location Point
                        if (fitting.Location is not LocationPoint locPoint)
                        {
                            skipReasons["no_location"]++;
                            continue;
                        }
                        XYZ point = locPoint.Point;

                        // 5. Find Floor Above (including links)
                        var floorInfo = GetFloorAbove(doc, point);
                        if (floorInfo.Point == null || floorInfo.DistanceFt <= 0)
                        {
                            skipReasons["no_floor_above"]++;
                            continue;
                        }

                        double distanceToFloorFt = floorInfo.DistanceFt;

                        // 6. Get Level
                        ElementId levelId = fitting.LevelId;
                        if (levelId == ElementId.InvalidElementId)
                        {
                            Parameter levelParam = fitting.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                            if (levelParam != null && levelParam.HasValue)
                            {
                                levelId = levelParam.AsElementId();
                            }
                        }
                        if (levelId == ElementId.InvalidElementId)
                        {
                            levelId = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();
                        }
                        if (levelId == ElementId.InvalidElementId)
                        {
                            skipReasons["no_level"]++;
                            continue;
                        }
                        Level level = (Level)doc.GetElement(levelId);

                        // 7. Get Length from parameter "ADSK_Размер_Длина" or containing "Длина"
                        double lengthMm = 0;
                        Parameter lengthParam = fitting.LookupParameter("ADSK_Размер_Длина");
                        if (lengthParam == null || !lengthParam.HasValue)
                        {
                            foreach (Parameter p in fitting.Parameters)
                            {
                                if (p.Definition.Name.Contains("ADSK_Размер_Длина") || p.Definition.Name.Contains("Длина"))
                                {
                                    if (p.HasValue)
                                    {
                                        lengthParam = p;
                                        break;
                                    }
                                }
                            }
                        }
                        if (lengthParam != null && lengthParam.HasValue)
                        {
                            lengthMm = lengthParam.AsDouble() * 304.8;
                        }

                        if (lengthMm <= 0)
                        {
                            skipReasons["no_length"]++;
                            continue;
                        }

                        // 8. Place Hanger(s)
                        double baseRotation = locPoint.Rotation;
                        XYZ direction = new XYZ(Math.Cos(baseRotation), Math.Sin(baseRotation), 0).Normalize();
                        XYZ perpDirection = new XYZ(-direction.Y, direction.X, 0).Normalize();

                        double stepFt = window.Step / 304.8;
                        double startEndOffsetFt = window.StartEndOffset / 304.8;
                        double lengthFt = lengthMm / 304.8;

                        double remLength = lengthFt - (2 * startEndOffsetFt);
                        List<double> offsets = new List<double>();

                        if (remLength <= 0)
                        {
                            offsets.Add(lengthFt / 2.0); // Place one in the center
                        }
                        else
                        {
                            int numSupports = (int)Math.Round(remLength / stepFt);
                            if (numSupports < 1) numSupports = 1;
                            double actualSpacing = remLength / numSupports;
                            for (int i = 0; i <= numSupports; i++)
                            {
                                offsets.Add(startEndOffsetFt + i * actualSpacing);
                            }
                        }

                        double widthOffsetFt = window.WidthOffset / 304.8;
                        double verticalOffsetFt = window.VerticalOffset / 304.8;

                        foreach (double offset in offsets)
                        {
                            XYZ supportPoint = point + direction * offset;
                            XYZ finalPt = supportPoint + perpDirection * widthOffsetFt + XYZ.BasisZ * verticalOffsetFt;

                            // Create family instance
                            FamilyInstance instance = doc.Create.NewFamilyInstance(finalPt, symbolToPlace, level, StructuralType.NonStructural);

                            // Set level parameter if possible
                            if (levelId != ElementId.InvalidElementId)
                            {
                                Parameter pLevel = instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                                if (pLevel != null && !pLevel.IsReadOnly)
                                {
                                    pLevel.Set(levelId);
                                }
                            }

                            // Rotate hanger to match fitting direction (+ 90 degrees + additional rotation)
                            double rotation = baseRotation + (Math.PI / 2.0) + window.AdditionalRotationAngle;
                            if (Math.Abs(rotation) > 0.001)
                            {
                                Line axis = Line.CreateBound(finalPt, finalPt + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
                            }

                            // Set "Высота подвеса" (Suspension Height)
                            Parameter pSuspension = instance.LookupParameter("Высота подвеса");
                            if (pSuspension != null && !pSuspension.IsReadOnly)
                            {
                                double suspensionHeightFt = distanceToFloorFt - verticalOffsetFt;
                                pSuspension.Set(suspensionHeightFt);
                            }

                            placedCount++;
                        }

                        processedCount++;
                    }

                    trans.Commit();
                }

                // Show result message
                string skipMessage = string.Join("\n", skipReasons
                    .Where(kvp => kvp.Value > 0)
                    .Select(kvp =>
                    {
                        switch (kvp.Key)
                        {
                            case "not_horizontal": return $"- Не горизонтальные детали: {kvp.Value}";
                            case "no_width_param": return $"- Нет параметра ширины лотка: {kvp.Value}";
                            case "no_family": return $"- Не найдено семейство крепления: {kvp.Value}";
                            case "no_location": return $"- Нет точки расположения: {kvp.Value}";
                            case "no_floor_above": return $"- Нет перекрытия сверху: {kvp.Value}";
                            case "no_level": return $"- Нет уровня: {kvp.Value}";
                            case "no_length": return $"- Нет параметра длины: {kvp.Value}";
                            default: return $"- Пропущено по другим причинам: {kvp.Value}";
                        }
                    }));

                TaskDialog.Show("GMDC - Расстановка креплений проволочного лотка", 
                    $"Обработано соединительных деталей: {processedCount}\n" +
                    $"Пропущено деталей: {targetFittings.Count - processedCount}\n" +
                    $"Размещено креплений: {placedCount}\n\n" +
                    (string.IsNullOrEmpty(skipMessage) ? "" : $"Причины пропуска деталей:\n{skipMessage}"));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Finds the nearest floor above the given point, searching in both the host and linked documents.
        /// </summary>
        private static (XYZ? Point, double DistanceFt, string? Source) GetFloorAbove(Document doc, XYZ point)
        {
            try
            {
                // Find any 3D view in the document (ReferenceIntersector requires a 3D view)
                View3D? view3D = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate);

                if (view3D == null) return (null, 0, null);

                var filter = new ElementCategoryFilter(BuiltInCategory.OST_Floors);
                var intersector = new ReferenceIntersector(filter, FindReferenceTarget.Face, view3D);
                intersector.FindReferencesInRevitLinks = true;

                XYZ rayDir = XYZ.BasisZ; // straight up
                ReferenceWithContext context = intersector.FindNearest(point, rayDir);
                if (context != null)
                {
                    Reference reference = context.GetReference();
                    double distanceFt = context.Proximity;
                    if (distanceFt > 0)
                    {
                        string source = "Текущая модель";
                        if (reference.LinkedElementId != ElementId.InvalidElementId)
                        {
                            var linkInstance = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                            if (linkInstance != null)
                            {
                                source = linkInstance.Name;
                            }
                        }
                        return (reference.GlobalPoint, distanceFt, source);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error finding floor above: {ex.Message}");
            }
            return (null, 0, null);
        }
    }
}
