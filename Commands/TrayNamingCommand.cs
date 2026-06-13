using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
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
                // Gather selections and all elements for the UI setup
                var selIds = uidoc.Selection.GetElementIds();
                var selectedTrays = selIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el is CableTray)
                    .ToList();

                var allTrays = new FilteredElementCollector(doc)
                    .OfClass(typeof(CableTray))
                    .ToList();

                // Open WPF dialog
                var window = new TrayNamingWindow(doc, selectedTrays, allTrays);
                
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (window.ShowDialog() != true || !window.IsRunRequested)
                {
                    return Result.Cancelled;
                }

                // Determine target elements based on chosen scope
                List<Element> targetTrays;
                switch (window.SelectedScope)
                {
                    case "Selected":
                        targetTrays = selectedTrays;
                        break;
                    case "View":
                        targetTrays = new FilteredElementCollector(doc, doc.ActiveView.Id)
                            .OfClass(typeof(CableTray))
                            .ToList();
                        break;
                    case "Project":
                        targetTrays = allTrays;
                        break;
                    default:
                        targetTrays = selectedTrays;
                        break;
                }

                if (!targetTrays.Any())
                {
                    TaskDialog.Show("GMDC Cable Tray Naming", "Не найдено лотков для обработки в выбранной области.");
                    return Result.Cancelled;
                }

                int successCount = 0;

                // Perform renaming in a Revit transaction
                using (var trans = new Transaction(doc, "GMDC - Наименование лотков"))
                {
                    trans.Start();

                    foreach (var tray in targetTrays)
                    {
                        bool renamed = false;
                        string resolvedName = TrayNamingWindow.ResolveTemplate(window.NameTemplate, tray);
                        if (Helpers.RevitUtils.SetParameterValue(tray, window.TargetParameter, resolvedName))
                        {
                            renamed = true;
                        }

                        // Optional length writing
                        bool lengthWritten = false;
                        if (window.WriteLength)
                        {
                            Parameter? srcLenParam = tray.LookupParameter(window.LengthSourceParam);
                            if (srcLenParam == null)
                            {
                                srcLenParam = tray.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                            }

                            if (srcLenParam != null)
                            {
                                double lengthInFeet = srcLenParam.AsDouble();
                                Parameter? tgtParam = tray.LookupParameter(window.LengthTargetParam);
                                if (tgtParam != null && !tgtParam.IsReadOnly)
                                {
                                    bool isLengthSpec = false;
                                    try
                                    {
                                        if (tgtParam.Definition.GetDataType() == SpecTypeId.Length)
                                        {
                                            isLengthSpec = true;
                                        }
                                    }
                                    catch {}

                                    if (isLengthSpec)
                                    {
                                        lengthWritten = tgtParam.Set(lengthInFeet);
                                    }
                                    else
                                    {
                                        double lengthInMeters = lengthInFeet * 0.3048;
                                        if (tgtParam.StorageType == StorageType.Double)
                                        {
                                            lengthWritten = tgtParam.Set(lengthInMeters);
                                        }
                                        else if (tgtParam.StorageType == StorageType.String)
                                        {
                                            lengthWritten = tgtParam.Set(lengthInMeters.ToString("F2"));
                                        }
                                        else if (tgtParam.StorageType == StorageType.Integer)
                                        {
                                            lengthWritten = tgtParam.Set((int)Math.Round(lengthInMeters));
                                        }
                                    }
                                }
                            }
                        }

                        if (renamed || lengthWritten)
                        {
                            successCount++;
                        }
                    }

                    trans.Commit();
                }

                TaskDialog.Show("GMDC Cable Tray Naming", $"Обработка завершена.\nОбработано лотков: {successCount} из {targetTrays.Count}.");
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
