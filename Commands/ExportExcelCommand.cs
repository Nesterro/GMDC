using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            string tempFolder = Path.GetTempPath();
            string tempFileName = Guid.NewGuid().ToString() + ".txt";
            string tempFilePath = Path.Combine(tempFolder, tempFileName);

            try
            {
                // Native Revit export options to export EXACT visual representation of the schedule
                var options = new ViewScheduleExportOptions
                {
                    FieldDelimiter = "\t",
                    Title = false,
                    ColumnHeaders = ExportColumnHeaders.OneRow,
                    HeadersFootersBlanks = true,
                    TextQualifier = ExportTextQualifier.DoubleQuote
                };

                schedule.Export(tempFolder, tempFileName, options);

                if (!File.Exists(tempFilePath)) return;

                // Read all exported text lines (encoded in UTF-8 by Revit)
                string[] lines = File.ReadAllLines(tempFilePath, Encoding.UTF8);
                try { File.Delete(tempFilePath); } catch { } // Cleanup immediately

                if (lines.Length == 0) return;

                // 1. Parse Headers from the first line of exported TSV (matches visual order 100%)
                List<string> headers = ParseCsvLine(lines[0]);
                int totalCols = headers.Count;
                int currentExcelRow = 1;

                // 2. Export Title
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

                // 3. Export Headers
                if (settings.ExportHeaders)
                {
                    for (int c = 0; c < totalCols; c++)
                    {
                        var cell = worksheet.Cell(currentExcelRow, c + 1);
                        cell.Value = headers[c];

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

                // 4. Export Data Body
                for (int r = 1; r < lines.Length; r++)
                {
                    List<string> cells = ParseCsvLine(lines[r]);
                    if (cells.Count == 0) continue;

                    // Check if this row is a group header (Revit exports group headers with empty rest of cells)
                    bool isGroupHeader = false;
                    if (cells.Count > 1 && !string.IsNullOrWhiteSpace(cells[0]))
                    {
                        isGroupHeader = true;
                        for (int c = 1; c < cells.Count; c++)
                        {
                            if (!string.IsNullOrWhiteSpace(cells[c]))
                            {
                                isGroupHeader = false;
                                break;
                            }
                        }
                    }

                    if (isGroupHeader)
                    {
                        // Write group header spanning across all columns
                        var cell = worksheet.Cell(currentExcelRow, 1);
                        cell.Value = cells[0];
                        cell.Style.Font.Bold = true;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                        if (totalCols > 1)
                        {
                            worksheet.Range(currentExcelRow, 1, currentExcelRow, totalCols).Merge();
                        }
                    }
                    else
                    {
                        // Write standard data row
                        for (int c = 0; c < totalCols; c++)
                        {
                            string text = c < cells.Count ? cells[c].Trim() : string.Empty;
                            if (text == "\"\"" || text == "\"")
                            {
                                text = string.Empty;
                            }

                            var cell = worksheet.Cell(currentExcelRow, c + 1);

                            if (string.IsNullOrEmpty(text))
                            {
                                cell.Value = string.Empty;
                            }
                            // Try parsing numbers to store them as actual numbers in Excel (instead of strings)
                            else if (double.TryParse(text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
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
                    }

                    worksheet.Row(currentExcelRow).Height = 20;
                    currentExcelRow++;
                }

                // 5. Autofit column widths
                if (settings.Autofit)
                {
                    worksheet.Columns(1, totalCols).AdjustToContents();
                    for (int c = 1; c <= totalCols; c++)
                    {
                        var col = worksheet.Column(c);
                        if (col.Width < 10) col.Width = 10;
                        else col.Width += 3;
                    }
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
                System.Diagnostics.Debug.WriteLine($"Error exporting schedule: {ex.Message}");
            }
        }

        private static List<string> ParseCsvLine(string line)
        {
            var cells = new List<string>();
            bool inQuotes = false;
            var currentCell = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes)
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            currentCell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        inQuotes = true;
                    }
                }
                else if (c == '\t' && !inQuotes)
                {
                    cells.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            cells.Add(currentCell.ToString());
            return cells;
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
