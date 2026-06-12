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
    public class HangerPlacementCommand : IExternalCommand
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

                if (!symbols.Any())
                {
                    TaskDialog.Show("GMDC Hanger Placement", "В проекте не найдено семейств категории 'Обобщенная модель'.\nПожалуйста, загрузите семейство крепления (кронштейн, подвес) перед запуском.");
                    return Result.Cancelled;
                }

                // Check selection
                var selIds = uidoc.Selection.GetElementIds();
                var selectedTrays = selIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el is CableTray)
                    .Cast<CableTray>()
                    .ToList();

                bool hasSelection = selectedTrays.Any();

                // Open WPF dialog
                var window = new HangerPlacementWindow(doc, symbols, hasSelection);
                
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (window.ShowDialog() != true || !window.IsPlaceRequested || window.SelectedSymbol == null)
                {
                    return Result.Cancelled;
                }

                // Gather target trays
                List<CableTray> targetTrays;
                switch (window.SelectedScope)
                {
                    case "Selected":
                        targetTrays = selectedTrays;
                        break;
                    case "View":
                        targetTrays = new FilteredElementCollector(doc, doc.ActiveView.Id)
                            .OfClass(typeof(CableTray))
                            .Cast<CableTray>()
                            .ToList();
                        break;
                    case "Project":
                        targetTrays = new FilteredElementCollector(doc)
                            .OfClass(typeof(CableTray))
                            .Cast<CableTray>()
                            .ToList();
                        break;
                    default:
                        targetTrays = selectedTrays;
                        break;
                }

                if (!targetTrays.Any())
                {
                    TaskDialog.Show("GMDC Hanger Placement", "Не найдено лотков для расстановки креплений.");
                    return Result.Cancelled;
                }

                var symbol = window.SelectedSymbol;
                var rules = window.SpacingRules.OrderBy(r => r.MaxWidth).ToList();
                int placedCount = 0;

                using (var trans = new Transaction(doc, "GMDC - Расстановка крепежа"))
                {
                    trans.Start();

                    // Activate family symbol if not active
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                    }

                    foreach (var tray in targetTrays)
                    {
                        // Check if tray has location curve
                        if (tray.Location is not LocationCurve locCurve) continue;
                        if (locCurve.Curve is not Line line) continue; // straight only

                        // Determine spacing (mm) based on width
                        Parameter widthParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                        double widthMm = widthParam != null ? Helpers.RevitUtils.FeetToMm(widthParam.AsDouble()) : 300;

                        double spacingMm = 1500; // default spacing if no rules match
                        foreach (var rule in rules)
                        {
                            if (widthMm <= rule.MaxWidth)
                            {
                                spacingMm = rule.Spacing;
                                break;
                            }
                        }

                        // Determine geometric parameters in feet
                        double length = line.Length;
                        double startOffsetFt = Helpers.RevitUtils.MmToFeet(window.StartOffset);
                        double endOffsetFt = Helpers.RevitUtils.MmToFeet(window.EndOffset);
                        double spacingFt = Helpers.RevitUtils.MmToFeet(spacingMm);

                        // Calculate placement coordinates
                        List<XYZ> points = new List<XYZ>();
                        double remLength = length - startOffsetFt - endOffsetFt;

                        if (remLength <= 0)
                        {
                            // Tray is too short to place multiple. Place one in the center
                            points.Add(line.Evaluate(0.5, true));
                        }
                        else
                        {
                            int divisions = (int)Math.Floor(remLength / spacingFt);
                            if (divisions < 1) divisions = 1;

                            double actualSpacing = remLength / divisions;
                            XYZ startPt = line.GetEndPoint(0);
                            XYZ direction = (line.GetEndPoint(1) - startPt).Normalize();

                            for (int i = 0; i <= divisions; i++)
                            {
                                XYZ pt = startPt + direction * (startOffsetFt + i * actualSpacing);
                                points.Add(pt);
                            }
                        }

                        // Read height to offset downwards (place hanger under the tray)
                        Parameter heightParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                        double heightFt = heightParam != null ? heightParam.AsDouble() : 0;
                        double zOffsetFt = Helpers.RevitUtils.MmToFeet(window.VerticalOffset) - (heightFt / 2.0);

                        // Get tray level
                        ElementId levelId = tray.LevelId;

                        // Calculate horizontal rotation angle of the tray
                        XYZ trayDirection = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                        double trayAngle = Math.Atan2(trayDirection.Y, trayDirection.X);
                        double finalAngle = trayAngle + window.AdditionalRotationAngle;

                        foreach (var pt in points)
                        {
                            XYZ finalPt = new XYZ(pt.X, pt.Y, pt.Z + zOffsetFt);
                            
                            // Place Hanger
                            FamilyInstance instance = doc.Create.NewFamilyInstance(finalPt, symbol, StructuralType.NonStructural);
                            
                            // Set level parameter
                            if (levelId != ElementId.InvalidElementId)
                            {
                                Parameter pLevel = instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                                if (pLevel != null && !pLevel.IsReadOnly)
                                {
                                    pLevel.Set(levelId);
                                }
                            }

                            // Rotate hanger to match tray direction + extra rotation
                            if (Math.Abs(finalAngle) > 0.001)
                            {
                                Line axis = Line.CreateBound(finalPt, finalPt + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, instance.Id, axis, finalAngle);
                            }

                            placedCount++;
                        }
                    }

                    trans.Commit();
                }

                TaskDialog.Show("GMDC Hanger Placement", $"Расстановка завершена.\nУспешно размещено креплений: {placedCount}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
