using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class WireTrayHangerWindow : Window
    {
        private readonly Document _doc;

        public bool IsPlaceRequested { get; private set; }
        public FamilySymbol? SelectedSymbol => (HangerFamilyCombo.SelectedItem as FamilySymbolWrapper)?.Symbol;
        public bool IsAutoFamily => AutoFamilyCheckBox.IsChecked == true;
        public string FittingFilter => TrayFittingFilterTextBox.Text.Trim();
        public string SelectedScope { get; private set; } = "Selected"; // "Selected", "View", "Project"
        public double Step { get; private set; } = 650;
        public double StartEndOffset { get; private set; } = 0;
        public double VerticalOffset { get; private set; } = 0;
        public double WidthOffset { get; private set; } = 0;
        public double AdditionalRotationAngle { get; private set; } = 0; // in radians

        public WireTrayHangerWindow(Document doc, List<FamilySymbol> symbols, bool hasSelection)
        {
            InitializeComponent();
            _doc = doc;

            // Initialize Scope options
            if (!hasSelection)
            {
                ScopeSelectedRadio.IsEnabled = false;
                ScopeViewRadio.IsChecked = true;
            }
            else
            {
                ScopeSelectedRadio.IsChecked = true;
            }

            // Populate Family combobox
            var wrappers = symbols
                .Select(s => new FamilySymbolWrapper { Symbol = s })
                .OrderBy(w => w.DisplayName)
                .ToList();

            HangerFamilyCombo.ItemsSource = wrappers;
            if (HangerFamilyCombo.Items.Count > 0)
            {
                HangerFamilyCombo.SelectedIndex = 0;
            }

            // Style the window on load
            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    WpfThemeHelper.ForceDarkTheme(this);
                }));
            };

            // Load default config
            LoadDefaultConfig();
        }

        private void AutoFamilyCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (HangerFamilyCombo != null)
            {
                HangerFamilyCombo.IsEnabled = AutoFamilyCheckBox.IsChecked == false;
            }
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            // Not used in this window, but keeping it clean.
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            var config = new WireTrayHangerConfig
            {
                IsAutoFamily = AutoFamilyCheckBox.IsChecked == true,
                FittingFilter = TrayFittingFilterTextBox.Text,
                Step = double.Parse(StepTextBox.Text),
                StartEndOffset = double.Parse(StartEndOffsetTextBox.Text),
                VerticalOffset = double.Parse(VerticalOffsetTextBox.Text),
                WidthOffset = double.Parse(WidthOffsetTextBox.Text),
                RotationIndex = RotationCombo.SelectedIndex,
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2),
                SelectedFamilyName = (HangerFamilyCombo.SelectedItem as FamilySymbolWrapper)?.DisplayName ?? string.Empty
            };

            SettingsManager.SaveConfig("wire_tray_hanger_config.json", config);
            string appData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDC");
            string filePath = System.IO.Path.Combine(appData, "wire_tray_hanger_config.json");
            MessageBox.Show(this, $"Конфигурация сохранена в:\n{filePath}", "GMDC", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var loaded = SettingsManager.LoadConfig("wire_tray_hanger_config.json", GetDefaultConfig());
            ApplyConfig(loaded);
            MessageBox.Show(this, "Конфигурация загружена!", "GMDC", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadDefaultConfig()
        {
            var loaded = SettingsManager.LoadConfig("wire_tray_hanger_config.json", GetDefaultConfig());
            ApplyConfig(loaded);
        }

        private WireTrayHangerConfig GetDefaultConfig()
        {
            return new WireTrayHangerConfig
            {
                IsAutoFamily = true,
                FittingFilter = "470_DKC_F5_Проволочный лоток",
                Step = 650,
                StartEndOffset = 0,
                VerticalOffset = 0,
                WidthOffset = 0,
                RotationIndex = 0,
                ScopeIndex = 0,
                SelectedFamilyName = string.Empty
            };
        }

        private void ApplyConfig(WireTrayHangerConfig config)
        {
            AutoFamilyCheckBox.IsChecked = config.IsAutoFamily;
            HangerFamilyCombo.IsEnabled = !config.IsAutoFamily;
            TrayFittingFilterTextBox.Text = config.FittingFilter;
            StepTextBox.Text = config.Step.ToString();
            StartEndOffsetTextBox.Text = config.StartEndOffset.ToString();
            VerticalOffsetTextBox.Text = config.VerticalOffset.ToString();
            WidthOffsetTextBox.Text = config.WidthOffset.ToString();
            RotationCombo.SelectedIndex = config.RotationIndex;

            if (config.ScopeIndex == 0 && ScopeSelectedRadio.IsEnabled)
            {
                ScopeSelectedRadio.IsChecked = true;
            }
            else if (config.ScopeIndex == 1)
            {
                ScopeViewRadio.IsChecked = true;
            }
            else if (config.ScopeIndex == 2)
            {
                ScopeProjectRadio.IsChecked = true;
            }

            if (!string.IsNullOrEmpty(config.SelectedFamilyName))
            {
                foreach (FamilySymbolWrapper item in HangerFamilyCombo.Items)
                {
                    if (item.DisplayName == config.SelectedFamilyName)
                    {
                        HangerFamilyCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private bool ValidateInputs()
        {
            if (AutoFamilyCheckBox.IsChecked == false && HangerFamilyCombo.SelectedItem == null)
            {
                MessageBox.Show(this, "Пожалуйста, выберите семейство крепления для ручной расстановки!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (string.IsNullOrEmpty(TrayFittingFilterTextBox.Text.Trim()))
            {
                MessageBox.Show(this, "Пожалуйста, введите фильтр имени семейства фитингов!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!double.TryParse(StepTextBox.Text, out double s) || s <= 0)
            {
                MessageBox.Show(this, "Шаг расстановки должен быть положительным числом!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!double.TryParse(StartEndOffsetTextBox.Text, out double _) ||
                !double.TryParse(VerticalOffsetTextBox.Text, out double _) ||
                !double.TryParse(WidthOffsetTextBox.Text, out double _))
            {
                MessageBox.Show(this, "Введены некорректные числовые значения отступов и смещений!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            Step = double.Parse(StepTextBox.Text);
            StartEndOffset = double.Parse(StartEndOffsetTextBox.Text);
            VerticalOffset = double.Parse(VerticalOffsetTextBox.Text);
            WidthOffset = double.Parse(WidthOffsetTextBox.Text);

            int rotIndex = RotationCombo.SelectedIndex;
            AdditionalRotationAngle = rotIndex * Math.PI / 2.0;

            if (ScopeSelectedRadio.IsChecked == true) SelectedScope = "Selected";
            else if (ScopeViewRadio.IsChecked == true) SelectedScope = "View";
            else SelectedScope = "Project";

            IsPlaceRequested = true;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class WireTrayHangerConfig
    {
        public bool IsAutoFamily { get; set; } = true;
        public string FittingFilter { get; set; } = "470_DKC_F5_Проволочный лоток";
        public double Step { get; set; } = 650;
        public double StartEndOffset { get; set; } = 0;
        public double VerticalOffset { get; set; } = 0;
        public double WidthOffset { get; set; } = 0;
        public int RotationIndex { get; set; } = 0;
        public int ScopeIndex { get; set; } = 0;
        public string SelectedFamilyName { get; set; } = string.Empty;
    }
}
