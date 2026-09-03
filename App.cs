using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using GMDC.Helpers;

namespace GMDC
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // Register AssemblyResolve event handler to load dependencies from the plugin directory
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

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

            // 7. Button 4: Cable Route
            var btnCableData = new PushButtonData(
                "CableRouteButton",
                "Кабель\nтрасса",
                assemblyPath,
                "GMDC.Commands.CableRouteCommand"
            )
            {
                ToolTip = "Прокладка трассы кабеля через лотки с созданием DirectShape.",
                LongDescription = "Строит 3D-трассу кабеля по выбранным лоткам и фитингам между двумя точками оборудования. Создаёт DirectShape с заполнением параметров (марка, наименование, длина).",
                LargeImage = IconGenerator.CreateCableIcon(32),
                Image      = IconGenerator.CreateCableIcon(16)
            };

            // 7.5. Button 5: Wire Tray Hanger Placement
            var btnWireTrayHangerData = new PushButtonData(
                "WireTrayHangerButton",
                "Проволочный\nлоток",
                assemblyPath,
                "GMDC.Commands.WireTrayHangerCommand"
            )
            {
                ToolTip = "Расстановка креплений на соединительных деталях проволочных лотков.",
                LongDescription = "Автоматически расставляет крепления на соединительных деталях (фитингах) проволочных лотков с заданным шагом, отступами и высотой подвеса до перекрытия.",
                LargeImage = IconGenerator.CreateHangerIcon(32),
                Image = IconGenerator.CreateHangerIcon(16)
            };

            // 7.6. Button 6: Dimension Placement
            var btnDimensionData = new PushButtonData(
                "DimensionPlacementButton",
                "Авто\nразмеры",
                assemblyPath,
                "GMDC.Commands.DimensionPlacementCommand"
            )
            {
                ToolTip = "Автоматическая расстановка каскадных размеров от стен до осей лотков и труб.",
                LongDescription = "Автоматически расставляет размеры от граней стен (включая связи) до осей инженерных сетей с каскадным выносом размерных линий наружу.",
                LargeImage = IconGenerator.CreateNamingIcon(32),
                Image = IconGenerator.CreateNamingIcon(16)
            };

            // 8. Add buttons to the Ribbon Panel
            panel.AddItem(btnExcelData);
            panel.AddItem(btnNamingData);
            panel.AddItem(btnHangerData);
            panel.AddItem(btnWireTrayHangerData);
            panel.AddItem(btnCableData);
            panel.AddItem(btnDimensionData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            return Result.Succeeded;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                // Get the name of the assembly being requested
                string assemblyName = new AssemblyName(args.Name).Name;
                
                // Get the directory of the GMDC assembly
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir)) return null;

                // Look for the DLL in the plugin directory
                string dllPath = Path.Combine(assemblyDir, assemblyName + ".dll");
                if (File.Exists(dllPath))
                {
                    return Assembly.LoadFrom(dllPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GMDC AssemblyResolve error: {ex.Message}");
            }
            return null;
        }
    }
}
