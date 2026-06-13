using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Autodesk.Revit.DB;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class ExportExcelWindow : Window
    {
        private readonly Document _doc;
        private readonly ObservableCollection<ScheduleItem> _allSchedules;
        private ICollectionView _schedulesView;

        public bool IsExportRequested { get; private set; }
        public List<ViewSchedule> SelectedSchedules => _allSchedules
            .Where(x => x.IsChecked)
            .Select(x => x.Schedule)
            .ToList();

        public ExcelExportSettings Settings { get; private set; } = new ExcelExportSettings();

        public ExportExcelWindow(Document doc, List<ViewSchedule> schedules)
        {
            InitializeComponent();
            _doc = doc;

            _allSchedules = new ObservableCollection<ScheduleItem>(
                schedules.Select(s => new ScheduleItem { Name = s.Name, Schedule = s, IsChecked = false })
            );

            _schedulesView = CollectionViewSource.GetDefaultView(_allSchedules);
            _schedulesView.Filter = FilterSchedules;
            SchedulesListBox.ItemsSource = _schedulesView;

            // Style the controls on load
            RoutedEventHandler controlLoaded = (s, e) =>
            {
                if (s is DependencyObject depObj)
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        WpfThemeHelper.ForceDarkTheme(depObj);
                    }));
                }
            };
            SearchTextBox.Loaded += controlLoaded;
            PathTextBox.Loaded += controlLoaded;
            HeaderColorCombo.Loaded += controlLoaded;
            HeaderFontSizeCombo.Loaded += controlLoaded;
            FontFamilyCombo.Loaded += controlLoaded;

            // Load default configuration if exists
            LoadDefaultConfig();
        }

        private bool FilterSchedules(object obj)
        {
            if (obj is ScheduleItem item)
            {
                if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
                    return true;

                return item.Name.Contains(SearchTextBox.Text, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _schedulesView.Refresh();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allSchedules)
            {
                if (FilterSchedules(item))
                {
                    item.IsChecked = true;
                }
            }
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allSchedules)
            {
                if (FilterSchedules(item))
                {
                    item.IsChecked = false;
                }
            }
        }

        private void ExportMode_Changed(object sender, RoutedEventArgs e)
        {
            if (PathTextBox == null) return;
            // Clear path as the selection mode changed (from file to directory or vice versa)
            PathTextBox.Text = string.Empty;
        }

        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            if (SingleFileRadio.IsChecked == true)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Excel Workbooks (*.xlsx)|*.xlsx",
                    Title = "Сохранить файл Excel",
                    DefaultExt = "xlsx",
                    FileName = "Экспорт_Ведомостей"
                };
                if (dialog.ShowDialog() == true)
                {
                    PathTextBox.Text = dialog.FileName;
                }
            }
            else
            {
                // .NET 8 OpenFolderDialog (Native Windows Folder Picker)
                var dialog = new OpenFolderDialog
                {
                    Title = "Выберите папку для сохранения таблиц"
                };
                if (dialog.ShowDialog() == true)
                {
                    PathTextBox.Text = dialog.FolderName;
                }
            }
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            UpdateSettingsFromUI();
            SettingsManager.SaveConfig("export_excel_config.json", Settings);
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDC");
            string filePath = Path.Combine(appData, "export_excel_config.json");
            MessageBox.Show(this, $"Конфигурация успешно сохранена в:\n{filePath}", "GMDC Excel Exporter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var loaded = SettingsManager.LoadConfig("export_excel_config.json", new ExcelExportSettings());
            ApplySettingsToUI(loaded);
            MessageBox.Show(this, "Конфигурация загружена!", "GMDC Excel Exporter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadDefaultConfig()
        {
            var loaded = SettingsManager.LoadConfig("export_excel_config.json", new ExcelExportSettings());
            ApplySettingsToUI(loaded);
        }

        private void UpdateSettingsFromUI()
        {
            Settings.SingleFile = SingleFileRadio.IsChecked == true;
            Settings.Path = PathTextBox.Text;
            Settings.Autofit = AutofitCheck.IsChecked == true;
            Settings.ShowGridlines = ShowGridlinesCheck.IsChecked == true;
            Settings.ExportTitle = ExportTitleCheck.IsChecked == true;
            Settings.ExportHeaders = ExportHeadersCheck.IsChecked == true;
            Settings.HeaderColorIndex = HeaderColorCombo.SelectedIndex;
            Settings.HeaderBold = HeaderBoldCheck.IsChecked == true;
            
            if (HeaderFontSizeCombo.SelectedItem is ComboBoxItem fontItem)
                Settings.HeaderFontSize = fontItem.Content.ToString() ?? "11";
            
            if (FontFamilyCombo.SelectedItem is ComboBoxItem familyItem)
                Settings.FontFamily = familyItem.Content.ToString() ?? "Arial";
        }

        private void ApplySettingsToUI(ExcelExportSettings settings)
        {
            Settings = settings;
            if (settings.SingleFile)
            {
                SingleFileRadio.IsChecked = true;
            }
            else
            {
                MultipleFilesRadio.IsChecked = true;
            }
            PathTextBox.Text = settings.Path;
            AutofitCheck.IsChecked = settings.Autofit;
            ShowGridlinesCheck.IsChecked = settings.ShowGridlines;
            ExportTitleCheck.IsChecked = settings.ExportTitle;
            ExportHeadersCheck.IsChecked = settings.ExportHeaders;
            HeaderColorCombo.SelectedIndex = settings.HeaderColorIndex;
            HeaderBoldCheck.IsChecked = settings.HeaderBold;

            // Set ComboBox items based on values
            foreach (ComboBoxItem item in HeaderFontSizeCombo.Items)
            {
                if (item.Content.ToString() == settings.HeaderFontSize)
                {
                    HeaderFontSizeCombo.SelectedItem = item;
                    break;
                }
            }
            foreach (ComboBoxItem item in FontFamilyCombo.Items)
            {
                if (item.Content.ToString() == settings.FontFamily)
                {
                    FontFamilyCombo.SelectedItem = item;
                    break;
                }
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!SelectedSchedules.Any())
            {
                MessageBox.Show(this, "Выберите хотя бы одну ведомость для экспорта!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(PathTextBox.Text))
            {
                MessageBox.Show(this, "Укажите путь для сохранения файлов!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Verify file extension or directory existence
            if (SingleFileRadio.IsChecked == true)
            {
                string? dir = Path.GetDirectoryName(PathTextBox.Text);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(this, "Указанная директория для сохранения файла не существует!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                if (!Directory.Exists(PathTextBox.Text))
                {
                    MessageBox.Show(this, "Указанная директория не существует!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            UpdateSettingsFromUI();
            IsExportRequested = true;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class ScheduleItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
            }
        }
        public string Name { get; set; } = string.Empty;
        public ViewSchedule Schedule { get; set; } = null!;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ExcelExportSettings
    {
        public bool SingleFile { get; set; } = true;
        public string Path { get; set; } = string.Empty;
        public bool Autofit { get; set; } = true;
        public bool ShowGridlines { get; set; } = true;
        public bool ExportTitle { get; set; } = true;
        public bool ExportHeaders { get; set; } = true;
        public int HeaderColorIndex { get; set; } = 0; // 0: Green, 1: Dark Gray, 2: Light Gray, 3: None
        public bool HeaderBold { get; set; } = true;
        public string HeaderFontSize { get; set; } = "11";
        public string FontFamily { get; set; } = "Arial";
    }
}
