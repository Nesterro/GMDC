using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;
using GMDC.UI;

namespace GMDC.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ExportExcelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Gather all view schedules (exclude revision schedules and template schedules)
                List<ViewSchedule> schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && !s.Name.Contains("Revision Schedule") && !s.Name.Contains("Ведомость изменений"))
                    .OrderBy(s => s.Name)
                    .ToList();

                if (!schedules.Any())
                {
                    TaskDialog.Show("GMDC Excel Exporter", "В проекте не найдено спецификаций (таблиц) для экспорта.");
                    return Result.Cancelled;
                }

                // Show WPF dialog
                var window = new ExportExcelWindow(doc, schedules);
                
                // Set owner to Revit window
                var helper = new System.Windows.Interop.WindowInteropHelper(window)
                {
                    Owner = uiapp.MainWindowHandle
                };

                if (window.ShowDialog() != true || !window.IsExportRequested)
                {
                    return Result.Cancelled;
                }

                var selectedSchedules = window.SelectedSchedules;
                var settings = window.Settings;

                if (settings.SingleFile)
                {
                    ExportToSingleFile(selectedSchedules, settings);
                }
                else
                {
                    ExportToSeparateFiles(selectedSchedules, settings);
                }

                TaskDialog.Show("GMDC Excel Exporter", $"Успешно экспортировано ведомостей: {selectedSchedules.Count}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ExportToSingleFile(List<ViewSchedule> schedules, ExcelExportSettings settings)
        {
            using (var workbook = new XLWorkbook())
            {
                foreach (var schedule in schedules)
                {
                    // Clean sheet name (Excel sheet name must be <= 31 chars and cannot contain: \ / ? * : [ ])
                    string sheetName = CleanSheetName(schedule.Name);
                    
                    // Avoid duplicate sheet names
                    int count = 1;
                    string baseName = sheetName;
                    while (workbook.Worksheets.Contains(sheetName))
                    {
                        string suffix = $"_{count}";
                        int maxLen = 31 - suffix.Length;
                        sheetName = (baseName.Length > maxLen ? baseName.Substring(0, maxLen) : baseName) + suffix;
                        count++;
                    }

                    var worksheet = workbook.Worksheets.Add(sheetName);
                    WriteScheduleToWorksheet(schedule, worksheet, settings);
                }

                workbook.SaveAs(settings.Path);
            }
        }

        private void ExportToSeparateFiles(List<ViewSchedule> schedules, ExcelExportSettings settings)
        {
            string folder = settings.Path;
            foreach (var schedule in schedules)
            {
                string safeName = CleanFileName(schedule.Name);
                string filePath = Path.Combine(folder, safeName + ".xlsx");

                // If file already exists, handle name collision
                int count = 1;
                while (File.Exists(filePath))
                {
                    filePath = Path.Combine(folder, $"{safeName}_{count++}.xlsx");
                }

                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Ведомость");
                    WriteScheduleToWorksheet(schedule, worksheet, settings);
                    workbook.SaveAs(filePath);
                }
            }
        }

        private void WriteScheduleToWorksheet(ViewSchedule schedule, IXLWorksheet worksheet, ExcelExportSettings settings)
        {
            // Global font setup
            worksheet.Style.Font.FontName = settings.FontFamily;
            worksheet.ShowGridLines = settings.ShowGridlines;

            TableData tableData = schedule.GetTableData();
            TableSectionData bodySection = tableData.GetSectionData(SectionType.Body);

            int totalRows = bodySection.NumberOfRows;
            int totalCols = bodySection.NumberOfColumns;

            // Fetch Headers from Schedule Field column headers (only visible ones)
            var visibleHeaders = new List<string>();
            ScheduleDefinition definition = schedule.Definition;
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField field = definition.GetField(i);
                if (!field.IsHidden)
                {
                    visibleHeaders.Add(field.ColumnHeading);
                }
            }

            // Fallback if mismatch
            while (visibleHeaders.Count < totalCols)
            {
                visibleHeaders.Add($"Столбец {visibleHeaders.Count + 1}");
            }

            int currentExcelRow = 1;

            // 1. Export Title
            if (settings.ExportTitle)
            {
                worksheet.Cell(currentExcelRow, 1).Value = schedule.Name;
                worksheet.Cell(currentExcelRow, 1).Style.Font.Bold = true;
                
                int titleSize = 11;
                if (int.TryParse(settings.HeaderFontSize, out int parsedSize))
                {
                    titleSize = parsedSize + 3;
                }
                worksheet.Cell(currentExcelRow, 1).Style.Font.FontSize = titleSize;
                worksheet.Cell(currentExcelRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                
                // Merge title over columns
                if (totalCols > 1)
                {
                    worksheet.Range(currentExcelRow, 1, currentExcelRow, totalCols).Merge();
                }

                currentExcelRow += 2; // title, then blank row
            }

            // 2. Export Headers
            if (settings.ExportHeaders)
            {
                for (int c = 0; c < totalCols; c++)
                {
                    var cell = worksheet.Cell(currentExcelRow, c + 1);
                    cell.Value = visibleHeaders[c];
                    
                    // Formatting headers
                    cell.Style.Font.Bold = settings.HeaderBold;
                    if (double.TryParse(settings.HeaderFontSize, out double fontSize))
                    {
                        cell.Style.Font.FontSize = fontSize;
                    }

                    // Apply Header Color
                    ApplyHeaderStyle(cell, settings.HeaderColorIndex);
                    
                    // Alignments
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    cell.Style.Alignment.WrapText = true;
                }
                
                // Set Header row height
                worksheet.Row(currentExcelRow).Height = 26;
                currentExcelRow++;
            }

            // 3. Export Data Body
            for (int r = 0; r < totalRows; r++)
            {
                for (int c = 0; c < totalCols; c++)
                {
                    string text = bodySection.GetCellText(r, c);
                    var cell = worksheet.Cell(currentExcelRow, c + 1);

                    // Try parsing numbers to store them as actual numbers in Excel (instead of strings)
                    if (double.TryParse(text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
                    {
                        cell.Value = num;
                    }
                    else
                    {
                        cell.Value = text;
                    }

                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    
                    // Draw clean thin borders for body cells
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D3D3D3");
                }
                
                worksheet.Row(currentExcelRow).Height = 20;
                currentExcelRow++;
            }

            // 4. Autofit column widths
            if (settings.Autofit)
            {
                worksheet.Columns(1, totalCols).AdjustToContents();
                // Add minor padding to widths to prevent ### or truncation in Excel
                for (int c = 1; c <= totalCols; c++)
                {
                    var col = worksheet.Column(c);
                    if (col.Width < 10) col.Width = 10;
                    else col.Width += 3;
                }
            }
        }

        private void ApplyHeaderStyle(IXLCell cell, int colorIndex)
        {
            // Set light gray thin borders on the header
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#2D2D30");

            switch (colorIndex)
            {
                case 0: // Green #3bb700
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#3bb700");
                    cell.Style.Font.FontColor = XLColor.White;
                    break;
                case 1: // Dark Gray
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#333333");
                    cell.Style.Font.FontColor = XLColor.White;
                    break;
                case 2: // Light Gray
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E5E5E5");
                    cell.Style.Font.FontColor = XLColor.Black;
                    break;
                default: // None (Transparent)
                    // No background, standard black text
                    cell.Style.Font.FontColor = XLColor.Black;
                    break;
            }
        }

        private string CleanSheetName(string name)
        {
            // Excel limits sheet names to 31 chars and bans: \ / ? * : [ ]
            char[] invalidChars = new[] { '\\', '/', '?', '*', ':', '[', ']' };
            string clean = name;
            foreach (char c in invalidChars)
            {
                clean = clean.Replace(c, '_');
            }
            if (clean.Length > 31)
            {
                clean = clean.Substring(0, 31);
            }
            return clean;
        }

        private string CleanFileName(string name)
        {
            // OS filename invalid chars
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string clean = name;
            foreach (char c in invalidChars)
            {
                clean = clean.Replace(c, '_');
            }
            return clean;
        }
    }
}
