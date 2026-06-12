using System;
using System.Reflection;
using Autodesk.Revit.UI;
using GMDC.Helpers;

namespace GMDC
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // 1. Create custom Ribbon Tab
            string tabName = "GMDC";
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Exception)
            {
                // Tab might already exist in some loading scenarios, which is fine.
            }

            // 2. Create custom Ribbon Panel inside the Tab
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "Инструменты GMDC");

            // 3. Get Assembly Path
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // 4. Button 1: Export Excel
            var btnExcelData = new PushButtonData(
                "ExportExcelButton",
                "Экспорт в\nExcel",
                assemblyPath,
                "GMDC.Commands.ExportExcelCommand"
            )
            {
                ToolTip = "Экспорт спецификаций и ведомостей в форматированные файлы Excel.",
                LongDescription = "Позволяет экспортировать одну или несколько таблиц из модели в Excel. Поддерживает настройку шрифта, сетки, цвета заголовков и сохранение конфигурации.",
                LargeImage = IconGenerator.CreateExcelIcon(32),
                Image = IconGenerator.CreateExcelIcon(16)
            };

            // 5. Button 2: Cable Tray Naming
            var btnNamingData = new PushButtonData(
                "TrayNamingButton",
                "Имя\nлотка",
                assemblyPath,
                "GMDC.Commands.TrayNamingCommand"
            )
            {
                ToolTip = "Заполнение наименования лотка по выбранным параметрам.",
                LongDescription = "Автоматически формирует наименования для кабельных лотков по настраиваемому текстовому шаблону с подстановкой геометрических и пользовательских параметров.",
                LargeImage = IconGenerator.CreateNamingIcon(32),
                Image = IconGenerator.CreateNamingIcon(16)
            };

            // 6. Button 3: Hanger Placement
            var btnHangerData = new PushButtonData(
                "HangerPlacementButton",
                "Крепеж\nлотка",
                assemblyPath,
                "GMDC.Commands.HangerPlacementCommand"
            )
            {
                ToolTip = "Автоматическая расстановка крепежа вдоль лотка.",
                LongDescription = "Расставляет семейства обобщенных моделей (подвесы, кронштейны) вдоль кабельных лотков с шагом, зависящим от их ширины, с поддержкой высотных и продольных отступов.",
                LargeImage = IconGenerator.CreateHangerIcon(32),
                Image = IconGenerator.CreateHangerIcon(16)
            };

            // 7. Add buttons to the Ribbon Panel
            panel.AddItem(btnExcelData);
            panel.AddItem(btnNamingData);
            panel.AddItem(btnHangerData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
