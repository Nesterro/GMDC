using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using GMDC.Helpers;
using GMDC.UI;

namespace GMDC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CableRouteCommand : IExternalCommand
    {
        // feet per millimetre
        private const double MmToFt = 1.0 / 304.8;
        private const double FtToM  = 0.3048;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument    uidoc = uiapp.ActiveUIDocument;
            Document      doc   = uidoc.Document;

            try
            {
                // Prompt user: Create new or Edit existing
                DirectShape existingDs = null;
                bool isEditing = false;

                XYZ pStart = null;
                XYZ pEnd = null;
                List<Element> routeElements = new List<Element>();
                List<ElementId> savedTrayIds = new List<ElementId>();

                // Initial parameters for UI
                string initLevel = "Силовой";
                string initMark = "W1";
                string initName = "ВВГнг(А)-FRLS 3х2.5";
                string initNameParam = "ADSK_Наименование";
                string initLengthParam = "ADSK_Количество";
                string initShapeMark = "Кабель";
                int initRowIndex = 0;
                double initRowStep = 50.0;
                double initCableSize = 15.0;
                double initReserve = 1.10;
                bool initRound = true;
                bool initUseGeneric = true;

                TaskDialog td = new TaskDialog("GMDC - Трассировка кабеля")
                {
                    MainInstruction = "Выберите действие:",
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Проложить новую трассу");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Редактировать существующую трассу");

                TaskDialogResult tdRes = td.Show();
                if (tdRes == TaskDialogResult.Cancel)
                {
                    return Result.Cancelled;
                }

                if (tdRes == TaskDialogResult.CommandLink2)
                {
                    // Select existing DirectShape
                    try
                    {
                        Reference rDs = uidoc.Selection.PickObject(
                            ObjectType.Element, 
                            new CableDirectShapeFilter(), 
                            "Выберите кабель (DirectShape) для редактирования");
                        existingDs = doc.GetElement(rDs.ElementId) as DirectShape;
                        if (existingDs != null)
                        {
                            isEditing = true;
                            string serialized = existingDs.ApplicationDataId;

                            bool deserialized = DeserializeRouteData(
                                serialized,
                                out pStart, out pEnd, out savedTrayIds,
                                out initLevel, out initMark, out initName, out initNameParam,
                                out initLengthParam, out initShapeMark, out initRowIndex,
                                out initRowStep, out initCableSize, out initReserve, out initRound,
                                out initUseGeneric);

                            if (deserialized)
                            {
                                foreach (var id in savedTrayIds)
                                {
                                    Element el = doc.GetElement(id);
                                    if (el != null) routeElements.Add(el);
                                }
                            }
                            else
                            {
                                pStart = null;
                                pEnd = null;
                                // Fallback parameter extraction
                                Parameter pLev = existingDs.LookupParameter("Комментарии");
                                if (pLev != null) initLevel = pLev.AsString() ?? "Силовой";
                                Parameter pMrk = existingDs.LookupParameter("ADSK_Марка");
                                if (pMrk != null) initMark = pMrk.AsString() ?? "W1";
                                Parameter pNam = existingDs.LookupParameter(initNameParam);
                                if (pNam != null) initName = pNam.AsString() ?? "ВВГнг(А)-FRLS 3х2.5";
                                Parameter pShp = existingDs.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                if (pShp != null) initShapeMark = pShp.AsString() ?? "Кабель";

                                TaskDialog.Show("GMDC", "Данные о геометрии кабеля не найдены. Точки и лотки потребуется выбрать заново.");
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                // If creating new, pick points and trays BEFORE showing the window
                if (!isEditing)
                {
                    // ── Step 1: pick START point ─────────────────────────────────
                    try
                    {
                        Reference refA = uidoc.Selection.PickObject(
                            ObjectType.PointOnElement,
                            "Выберите НАЧАЛО трассы (оборудование в проекте или связи)");
                        pStart = CableRouteGeometry.GetTopCenterPoint(doc, refA);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                    if (pStart == null)
                    {
                        TaskDialog.Show("GMDC - Кабель", "Не удалось определить координаты начальной точки.");
                        return Result.Failed;
                    }

                    // ── Step 2: pick END point ───────────────────────────────────
                    try
                    {
                        Reference refB = uidoc.Selection.PickObject(
                            ObjectType.PointOnElement,
                            "Выберите КОНЕЦ трассы (оборудование в проекте или связи)");
                        pEnd = CableRouteGeometry.GetTopCenterPoint(doc, refB);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                    if (pEnd == null)
                    {
                        TaskDialog.Show("GMDC - Кабель", "Не удалось определить координаты конечной точки.");
                        return Result.Failed;
                    }

                    // ── Step 3: pick route elements (trays / fittings) ────────────
                    try
                    {
                        var routeRefs = uidoc.Selection.PickObjects(
                            ObjectType.Element,
                            new TrayAndFittingFilter(),
                            "Выберите лотки и фитинги трассы");
                        routeElements = routeRefs
                            .Select(r => doc.GetElement(r.ElementId))
                            .Where(el => el != null)
                            .ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                    if (!routeElements.Any())
                    {
                        TaskDialog.Show("GMDC - Кабель", "Не выбрано ни одного лотка или фитинга.");
                        return Result.Failed;
                    }
                }

                // ── Step 4: show settings window ──────────────────────────────
                var window = new CableRouteWindow();
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (isEditing)
                {
                    window.EnableEditMode();
                    window.LoadFromData(initLevel, initMark, initName, initNameParam, initLengthParam, initShapeMark,
                                        initRowIndex, initRowStep, initCableSize, initReserve, initRound, initUseGeneric);
                }

                if (window.ShowDialog() != true || !window.IsRunRequested)
                    return Result.Cancelled;

                // ── Step 4.5: Re-pick if requested during edit ──────────────────
                if (isEditing)
                {
                    if (window.RepickPoints || pStart == null || pEnd == null)
                    {
                        try
                        {
                            Reference refA = uidoc.Selection.PickObject(
                                ObjectType.PointOnElement,
                                "Выберите НАЧАЛО трассы (оборудование в проекте или связи)");
                            pStart = CableRouteGeometry.GetTopCenterPoint(doc, refA);
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                        try
                        {
                            Reference refB = uidoc.Selection.PickObject(
                                ObjectType.PointOnElement,
                                "Выберите КОНЕЦ трассы (оборудование в проекте или связи)");
                            pEnd = CableRouteGeometry.GetTopCenterPoint(doc, refB);
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                    }

                    if (window.RepickTrays || routeElements.Count == 0)
                    {
                        try
                        {
                            var routeRefs = uidoc.Selection.PickObjects(
                                ObjectType.Element,
                                new TrayAndFittingFilter(),
                                "Выберите лотки и фитинги трассы");
                            routeElements = routeRefs
                                .Select(r => doc.GetElement(r.ElementId))
                                .Where(el => el != null)
                                .ToList();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                    }
                }

                // ── Step 5: build geometry ────────────────────────────────────
                double offsetFt = window.RowIndex * window.RowStepMm * MmToFt;
                double halfSizeFt = (window.CableSizeMm / 2.0) * MmToFt;

                var (finalCurves, lengthFt) = CableRouteGeometry.BuildRoute(
                    doc, routeElements, pStart, pEnd, offsetFt);

                if (finalCurves == null || finalCurves.Count == 0)
                {
                    TaskDialog.Show("GMDC - Кабель", "Не удалось построить трассу. Проверьте выбранные элементы.");
                    return Result.Failed;
                }

                var solids = CableRouteGeometry.BuildSolids(finalCurves, halfSizeFt);

                if (solids.Count == 0)
                {
                    TaskDialog.Show("GMDC - Кабель", "Не удалось создать 3D-объём кабеля.");
                    return Result.Failed;
                }

                // ── Step 6: calculate length ──────────────────────────────────
                double lengthM = lengthFt * FtToM;
                double specLength = lengthM * window.ReserveCoeff;
                if (window.RoundLength) specLength = Math.Round(specLength, 1);

                // Serialize data for future editing
                var currentTrayIds = routeElements.Select(el => el.Id).ToList();
                string serializedData = SerializeRouteData(
                    pStart, pEnd, currentTrayIds,
                    window.CableLevel, window.CableMark, window.CableName, window.NameParam,
                    window.LengthParam, window.ShapeMark, window.RowIndex, window.RowStepMm,
                    window.CableSizeMm, window.ReserveCoeff, window.RoundLength, window.UseGenericModel);

                // ── Step 7: create or update DirectShape in transaction ─────────
                BuiltInCategory cat = window.UseGenericModel
                    ? BuiltInCategory.OST_GenericModel
                    : BuiltInCategory.OST_CableTray;

                using (var t = new Transaction(doc, isEditing ? "GMDC - Обновление трассы кабеля" : "GMDC - Прокладка кабеля"))
                {
                    t.Start();

                    DirectShape ds;
                    if (isEditing && existingDs != null)
                    {
                        ds = existingDs;
                        // Change category if changed in UI
                        if (ds.Category.Id.Value != (int)cat)
                        {
                            ds.SetTypeId(new ElementId(cat));
                        }
                    }
                    else
                    {
                        ds = DirectShape.CreateElement(doc, new ElementId(cat));
                    }

                    ds.ApplicationId     = "GMDC_CableRoute";
                    ds.ApplicationDataId = serializedData;

                    var geoList = new List<GeometryObject>(solids);
                    ds.SetShape(geoList);

                    // Fill parameters
                    SetParam(ds, "Комментарии",      window.CableLevel);
                    SetParam(ds, "ADSK_Марка",        window.CableMark);
                    SetParam(ds, "Марка",             window.ShapeMark,  isMarkParam: true);
                    SetParam(ds, window.NameParam,    window.CableName);
                    SetParam(ds, window.LengthParam,  specLength.ToString("F2"), isNumber: true);

                    t.Commit();
                }

                TaskDialog.Show("GMDC - Кабель",
                    (isEditing ? "Трасса успешно обновлена!\n\n" : "Трасса успешно создана!\n\n") +
                    $"Физическая длина: {lengthM:F2} м\n" +
                    $"Расчётная длина (×{window.ReserveCoeff:F2}): {specLength:F2} м\n" +
                    $"Записано в параметр: {window.LengthParam}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Parameter writing helpers
        // ─────────────────────────────────────────────────────────────────

        private static void SetParam(Element el, string paramName, string value,
                                     bool isNumber = false, bool isMarkParam = false)
        {
            Parameter p = el.LookupParameter(paramName);

            if (p == null && isMarkParam)
                p = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);

            if (p == null || p.IsReadOnly) return;

            try
            {
                if (isNumber && double.TryParse(value, System.Globalization.NumberStyles.Any,
                                                 System.Globalization.CultureInfo.InvariantCulture,
                                                 out double dv))
                    p.Set(dv);
                else
                    p.Set(value);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        // Serialization & Deserialization of Route Data
        // ─────────────────────────────────────────────────────────────────

        private static string SerializeRouteData(XYZ start, XYZ end, List<ElementId> trayIds,
                                                 string level, string mark, string name, string nameParam,
                                                 string lengthParam, string shapeMark, int rowIndex,
                                                 double rowStep, double size, double reserve, bool round,
                                                 bool useGenericModel)
        {
            string startStr = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F6},{1:F6},{2:F6}", start.X, start.Y, start.Z);
            string endStr = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F6},{1:F6},{2:F6}", end.X, end.Y, end.Z);
            string traysStr = string.Join(",", trayIds.Select(id => id.Value.ToString()));

            return $"START:{startStr}|" +
                   $"END:{endStr}|" +
                   $"TRAYS:{traysStr}|" +
                   $"LEVEL:{level}|" +
                   $"MARK:{mark}|" +
                   $"NAME:{name}|" +
                   $"NAMEPARAM:{nameParam}|" +
                   $"LENGTHPARAM:{lengthParam}|" +
                   $"SHAPEMARK:{shapeMark}|" +
                   $"ROW:{rowIndex}|" +
                   $"ROWSTEP:{rowStep.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|" +
                   $"SIZE:{size.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|" +
                   $"RESERVE:{reserve.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|" +
                   $"ROUND:{(round ? "1" : "0")}|" +
                   $"GENERIC:{(useGenericModel ? "1" : "0")}";
        }

        private static bool DeserializeRouteData(string data,
                                                 out XYZ start, out XYZ end, out List<ElementId> trayIds,
                                                 out string level, out string mark, out string name, out string nameParam,
                                                 out string lengthParam, out string shapeMark, out int rowIndex,
                                                 out double rowStep, out double size, out double reserve, out bool round,
                                                 out bool useGenericModel)
        {
            start = null;
            end = null;
            trayIds = new List<ElementId>();
            level = "Силовой";
            mark = "W1";
            name = "ВВГнг(А)-FRLS 3х2.5";
            nameParam = "ADSK_Наименование";
            lengthParam = "ADSK_Количество";
            shapeMark = "Кабель";
            rowIndex = 0;
            rowStep = 50.0;
            size = 15.0;
            reserve = 1.10;
            round = true;
            useGenericModel = true;

            if (string.IsNullOrEmpty(data)) return false;

            try
            {
                var parts = data.Split('|');
                foreach (var part in parts)
                {
                    if (part.StartsWith("START:"))
                    {
                        var coords = part.Substring(6).Split(',');
                        start = new XYZ(double.Parse(coords[0], System.Globalization.CultureInfo.InvariantCulture),
                                        double.Parse(coords[1], System.Globalization.CultureInfo.InvariantCulture),
                                        double.Parse(coords[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (part.StartsWith("END:"))
                    {
                        var coords = part.Substring(4).Split(',');
                        end = new XYZ(double.Parse(coords[0], System.Globalization.CultureInfo.InvariantCulture),
                                      double.Parse(coords[1], System.Globalization.CultureInfo.InvariantCulture),
                                      double.Parse(coords[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else if (part.StartsWith("TRAYS:"))
                    {
                        var ids = part.Substring(6).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var idStr in ids)
                        {
                            trayIds.Add(new ElementId(long.Parse(idStr)));
                        }
                    }
                    else if (part.StartsWith("LEVEL:"))      level = part.Substring(6);
                    else if (part.StartsWith("MARK:"))       mark = part.Substring(5);
                    else if (part.StartsWith("NAME:"))       name = part.Substring(5);
                    else if (part.StartsWith("NAMEPARAM:"))  nameParam = part.Substring(10);
                    else if (part.StartsWith("LENGTHPARAM:")) lengthParam = part.Substring(12);
                    else if (part.StartsWith("SHAPEMARK:"))  shapeMark = part.Substring(10);
                    else if (part.StartsWith("ROW:"))        rowIndex = int.Parse(part.Substring(4));
                    else if (part.StartsWith("ROWSTEP:"))    rowStep = double.Parse(part.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
                    else if (part.StartsWith("SIZE:"))       size = double.Parse(part.Substring(5), System.Globalization.CultureInfo.InvariantCulture);
                    else if (part.StartsWith("RESERVE:"))    reserve = double.Parse(part.Substring(8), System.Globalization.CultureInfo.InvariantCulture);
                    else if (part.StartsWith("ROUND:"))      round = part.Substring(6) == "1";
                    else if (part.StartsWith("GENERIC:"))    useGenericModel = part.Substring(8) == "1";
                }
                return start != null && end != null;
            }
            catch
            {
                return false;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Selection filter: DirectShapes with specific characteristics
    // ─────────────────────────────────────────────────────────────────
    internal class CableDirectShapeFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is DirectShape ds)
            {
                return ds.ApplicationId == "GMDC_CableRoute" || ds.Name.Contains("Кабель") || ds.Name.Contains("Cable");
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    // ─────────────────────────────────────────────────────────────────
    // Selection filter: cable trays + MEP fittings
    // ─────────────────────────────────────────────────────────────────
    internal class TrayAndFittingFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;
            Category cat = elem.Category;
            if (cat == null) return false;
            BuiltInCategory bic = (BuiltInCategory)(int)cat.Id.Value;
            return bic == BuiltInCategory.OST_CableTray
                || bic == BuiltInCategory.OST_CableTrayFitting
                || bic == BuiltInCategory.OST_DuctCurves
                || bic == BuiltInCategory.OST_DuctFitting;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
