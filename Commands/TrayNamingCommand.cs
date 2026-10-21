using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using GMDC.Helpers;
using GMDC.UI;

namespace GMDC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class TrayNamingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                var selectedElements = uidoc.Selection.GetElementIds()
                    .Select(doc.GetElement)
                    .Where(el => el is CableTray || el is FamilyInstance)
                    .ToList();
                var allTrays = new FilteredElementCollector(doc).OfClass(typeof(CableTray)).ToList();
                var viewTrays = FilteredElementCollector.IsViewValidForElementIteration(doc, doc.ActiveView.Id)
                    ? new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(CableTray)).ToList()
                    : new List<Element>();
                var window = new TrayNamingWindow(doc, selectedElements, viewTrays, allTrays);
                var helper = new System.Windows.Interop.WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
                if (window.ShowDialog() != true || !window.IsRunRequested) return Result.Cancelled;
                var targets = window.SelectedScope == "View" ? viewTrays
                    : window.SelectedScope == "Project" ? allTrays : selectedElements;
                if (targets.Count == 0)
                {
                    TaskDialog.Show("GMDC Cable Tray Naming", "Нет подходящих элементов в выбранной области. Выберите лотки или экземпляры семейств.");
                    return Result.Cancelled;
                }
                int named = 0, lengths = 0;
                var issues = new List<string>();
                using (var trans = new Transaction(doc, "GMDC - Наименование элементов"))
                {
                    trans.Start();
                    foreach (var element in targets)
                    {
                        using var itemTransaction = new SubTransaction(doc);
                        itemTransaction.Start();
                        try
                        {
                            var target = element.LookupParameter(window.TargetParameter);
                            bool nameWritten = target != null && !target.IsReadOnly && target.StorageType == StorageType.String
                                && target.Set(TrayNamingWindow.ResolveTemplate(window.NameTemplate, element));
                            bool lengthWritten = window.WriteLength && WriteElementLength(element, window.LengthSourceParam, window.LengthTargetParam);
                            itemTransaction.Commit();
                            if (nameWritten) named++;
                            if (lengthWritten) lengths++;
                            if (!nameWritten || (window.WriteLength && !lengthWritten))
                                issues.Add($"ID {element.Id}: " + (!nameWritten ? "нет доступного текстового параметра назначения; " : "")
                                    + (window.WriteLength && !lengthWritten ? "длина не записана (проверьте источник и назначение)" : ""));
                        }
                        catch (Exception ex)
                        {
                            if (itemTransaction.GetStatus() == TransactionStatus.Started) itemTransaction.RollBack();
                            issues.Add($"ID {element.Id}: {ex.Message}");
                        }
                    }
                    if (trans.Commit() != TransactionStatus.Committed)
                    {
                        message = "Revit отменил запись параметров.";
                        return Result.Failed;
                    }
                }
                var report = new TaskDialog("GMDC Cable Tray Naming")
                {
                    MainInstruction = "Обработка завершена",
                    MainContent = $"Элементов: {targets.Count}.\nЗаписано наименований: {named}."
                        + (window.WriteLength ? $"\nЗаписано значений длины: {lengths}." : "")
                        + $"\nЭлементов с пропусками или ошибками: {issues.Count}."
                };
                if (issues.Count > 0) report.ExpandedContent = string.Join("\n", issues.Take(30))
                    + (issues.Count > 30 ? $"\n…ещё {issues.Count - 30}." : "");
                report.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool WriteElementLength(Element element, string sourceName, string targetName)
        {
            var source = RevitUtils.LookupParameterInElementOrType(element, sourceName);
            // Preserve the localized system-length fallback for cable trays only.
            if (source == null && element is CableTray)
                source = element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (source == null || !source.HasValue || source.StorageType != StorageType.Double
                || source.Definition.GetDataType() != SpecTypeId.Length) return false;
            var target = element.LookupParameter(targetName);
            if (target == null || target.IsReadOnly) return false;
            double feet = source.AsDouble();
            if (target.StorageType == StorageType.Double && target.Definition.GetDataType() == SpecTypeId.Length)
                return target.Set(feet);
            double meters = feet * 0.3048;
            if (target.StorageType == StorageType.Double && target.Definition.GetDataType() == SpecTypeId.Number)
                return target.Set(meters);
            if (target.StorageType == StorageType.String) return target.Set(meters.ToString("F2"));
            if (target.StorageType == StorageType.Integer && target.Definition.GetDataType() == SpecTypeId.Int.Integer)
                return target.Set((int)Math.Round(meters));
            return false;
        }
    }
}
