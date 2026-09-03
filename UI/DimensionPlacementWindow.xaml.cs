using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class DimensionPlacementWindow : Window
    {
        private readonly Document _doc;

        public bool IsPlaceRequested { get; private set; }
        public DimensionType? SelectedStyle => DimStyleCombo.SelectedItem as DimensionType;

        public bool CatConduits => CatConduitsCheck.IsChecked == true;
        public bool CatCableTrays => CatCableTraysCheck.IsChecked == true;
        public bool CatPipes => CatPipesCheck.IsChecked == true;
        public bool CatDucts => CatDuctsCheck.IsChecked == true;

        public string SelectedScope { get; private set; } = "Selected"; // "Selected", "View", "Project"

        public double MaxDistance { get; private set; } = 12000;
        public double FirstOffset { get; private set; } = 1500;
        public double CascadeStep { get; private set; } = 600;
        public int CascadeLevels { get; private set; } = 4;
        public double MinDim { get; private set; } = 15;
        public double DedupTol { get; private set; } = 2;

        public DimensionPlacementWindow(Document doc, List<DimensionType> styles, bool hasSelection)
        {
            InitializeComponent();
            _doc = doc;

            // Set up scope selection states
            if (!hasSelection)
            {
                ScopeSelectedRadio.IsEnabled = false;
                ScopeViewRadio.IsChecked = true;
            }
            else
            {
                ScopeSelectedRadio.IsChecked = true;
            }

            // Populate Dimension Styles combobox
            DimStyleCombo.ItemsSource = styles;
            if (DimStyleCombo.Items.Count > 0)
            {
                // Default to a Gost style if available, otherwise index 0
                int gostIdx = styles.FindIndex(s => s.Name.Contains("ГОСТ") || s.Name.Contains("Gost") || s.Name.Contains("1:50") || s.Name.Contains("1-50"));
                DimStyleCombo.SelectedIndex = gostIdx != -1 ? gostIdx : 0;
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

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            var config = new DimensionConfig
            {
                CatConduits = CatConduitsCheck.IsChecked == true,
                CatCableTrays = CatCableTraysCheck.IsChecked == true,
                CatPipes = CatPipesCheck.IsChecked == true,
                CatDucts = CatDuctsCheck.IsChecked == true,
                SelectedStyleName = (DimStyleCombo.SelectedItem as DimensionType)?.Name ?? string.Empty,
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2),
                MaxDistance = double.Parse(MaxDistanceTextBox.Text),
                FirstOffset = double.Parse(FirstOffsetTextBox.Text),
                CascadeStep = double.Parse(CascadeStepTextBox.Text),
                CascadeLevels = int.Parse(CascadeLevelsTextBox.Text),
                MinDim = double.Parse(MinDimTextBox.Text),
                DedupTol = double.Parse(DedupTolTextBox.Text)
            };

            SettingsManager.SaveConfig("dimension_placement_config.json", config);
            string appData = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDC");
            string filePath = System.IO.Path.Combine(appData, "dimension_placement_config.json");
            MessageBox.Show(this, $"Конфигурация размеров сохранена в:\n{filePath}", "GMDC AutoDim", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var loaded = SettingsManager.LoadConfig("dimension_placement_config.json", GetDefaultConfig());
            ApplyConfig(loaded);
            MessageBox.Show(this, "Конфигурация размеров загружена!", "GMDC AutoDim", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadDefaultConfig()
        {
            var loaded = SettingsManager.LoadConfig("dimension_placement_config.json", GetDefaultConfig());
            ApplyConfig(loaded);
        }

        private DimensionConfig GetDefaultConfig()
        {
            return new DimensionConfig
            {
                CatConduits = true,
                CatCableTrays = true,
                CatPipes = false,
                CatDucts = false,
                SelectedStyleName = string.Empty,
                ScopeIndex = 0,
                MaxDistance = 12000,
                FirstOffset = 1500,
                CascadeStep = 600,
                CascadeLevels = 4,
                MinDim = 15,
                DedupTol = 2
            };
        }

        private void ApplyConfig(DimensionConfig config)
        {
            CatConduitsCheck.IsChecked = config.CatConduits;
            CatCableTraysCheck.IsChecked = config.CatCableTrays;
            CatPipesCheck.IsChecked = config.CatPipes;
            CatDuctsCheck.IsChecked = config.CatDucts;

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

            if (!string.IsNullOrEmpty(config.SelectedStyleName))
            {
                foreach (DimensionType item in DimStyleCombo.Items)
                {
                    if (item.Name == config.SelectedStyleName)
                    {
                        DimStyleCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            MaxDistanceTextBox.Text = config.MaxDistance.ToString();
            FirstOffsetTextBox.Text = config.FirstOffset.ToString();
            CascadeStepTextBox.Text = config.CascadeStep.ToString();
            CascadeLevelsTextBox.Text = config.CascadeLevels.ToString();
            MinDimTextBox.Text = config.MinDim.ToString();
            DedupTolTextBox.Text = config.DedupTol.ToString();
        }

        private bool ValidateInputs()
        {
            if (DimStyleCombo.SelectedItem == null)
            {
                MessageBox.Show(this, "Пожалуйста, выберите стиль размера!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!CatConduits && !CatCableTrays && !CatPipes && !CatDucts)
            {
                MessageBox.Show(this, "Пожалуйста, выберите хотя бы одну категорию инженерных сетей!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!double.TryParse(MaxDistanceTextBox.Text, out double md) || md <= 0 ||
                !double.TryParse(FirstOffsetTextBox.Text, out double fo) || fo <= 0 ||
                !double.TryParse(CascadeStepTextBox.Text, out double cs) || cs <= 0 ||
                !int.TryParse(CascadeLevelsTextBox.Text, out int cl) || cl <= 0 ||
                !double.TryParse(MinDimTextBox.Text, out double mind) || mind < 0 ||
                !double.TryParse(DedupTolTextBox.Text, out double dt) || dt < 0)
            {
                MessageBox.Show(this, "Введены некорректные числовые параметры!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            MaxDistance = double.Parse(MaxDistanceTextBox.Text);
            FirstOffset = double.Parse(FirstOffsetTextBox.Text);
            CascadeStep = double.Parse(CascadeStepTextBox.Text);
            CascadeLevels = int.Parse(CascadeLevelsTextBox.Text);
            MinDim = double.Parse(MinDimTextBox.Text);
            DedupTol = double.Parse(DedupTolTextBox.Text);

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

    public class DimensionConfig
    {
        public bool CatConduits { get; set; } = true;
        public bool CatCableTrays { get; set; } = true;
        public bool CatPipes { get; set; } = false;
        public bool CatDucts { get; set; } = false;
        public string SelectedStyleName { get; set; } = string.Empty;
        public int ScopeIndex { get; set; } = 0;
        public double MaxDistance { get; set; } = 12000;
        public double FirstOffset { get; set; } = 1500;
        public double CascadeStep { get; set; } = 600;
        public int CascadeLevels { get; set; } = 4;
        public double MinDim { get; set; } = 15;
        public double DedupTol { get; set; } = 2;
    }
}
