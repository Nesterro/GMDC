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
            try
            {
                // Register AssemblyResolve event handlers to load dependencies from the plugin directory
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

#if NET8_0_OR_GREATER
                try
                {
                    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                string path = Path.Combine(dir, assemblyName.Name + ".dll");
                                if (File.Exists(path))
                                {
                                    return context.LoadFromAssemblyPath(path);
                                }
                            }
                        }
                        catch { }
                        return null;
                    };
                }
                catch { }
#endif

                // 1. Create or ensure custom Ribbon Tab
                string tabName = "GMDC";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch (Exception)
                {
                    // Tab might already exist in some loading scenarios, which is fine.
                }

                // 2. Find or create Ribbon Panel inside the Tab
                RibbonPanel panel = null;
                try
                {
                    var panels = application.GetRibbonPanels(tabName);
                    if (panels != null)
                    {
                        foreach (var p in panels)
                        {
                            if (p.Name == "Инструменты GMDC")
                            {
                                panel = p;
                                break;
                            }
                        }
                    }
                }
                catch { }

                if (panel == null)
                {
                    try
                    {
                        panel = application.CreateRibbonPanel(tabName, "Инструменты GMDC");
                    }
                    catch
                    {
                        try
                        {
                            panel = application.CreateRibbonPanel("Инструменты GMDC");
                        }
                        catch { }
                    }
                }

                if (panel == null)
                {
                    return Result.Failed;
                }

                // 3. Get Assembly Path
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // 4. Button 1: Export Excel
                try
                {
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
                    panel.AddItem(btnExcelData);
                }
                catch (Exception ex)
                {
                    LogStartupError("ExportExcelButton", ex);
                }

                // 5. Button 2: Cable Tray Naming
                try
                {
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
                    panel.AddItem(btnNamingData);
                }
                catch (Exception ex)
                {
                    LogStartupError("TrayNamingButton", ex);
                }

                // 6. Button 3: Hanger Placement
                try
                {
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
                    panel.AddItem(btnHangerData);
                }
                catch (Exception ex)
                {
                    LogStartupError("HangerPlacementButton", ex);
                }

                // 7. Button 4: Wire Tray Hanger Placement
                try
                {
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
                    panel.AddItem(btnWireTrayHangerData);
                }
                catch (Exception ex)
                {
                    LogStartupError("WireTrayHangerButton", ex);
                }

                // 8. Button 5: Cable Route
                try
                {
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
                        Image = IconGenerator.CreateCableIcon(16)
                    };
                    panel.AddItem(btnCableData);
                }
                catch (Exception ex)
                {
                    LogStartupError("CableRouteButton", ex);
                }

                // 9. Button 6: Dimension Placement
                try
                {
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
                    panel.AddItem(btnDimensionData);
                }
                catch (Exception ex)
                {
                    LogStartupError("DimensionPlacementButton", ex);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                LogStartupError("OnStartup_Fatal", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            }
            catch { }
            return Result.Succeeded;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string assemblyName = new AssemblyName(args.Name).Name;
                string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir)) return null;

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

        private static void LogStartupError(string source, Exception ex)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string logDir = Path.Combine(appData, "GMDC");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "startup_error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now}] [{source}] {ex}\n");
            }
            catch { }
        }
    }
}
