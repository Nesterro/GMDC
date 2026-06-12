using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class HangerPlacementWindow : Window
    {
        private readonly Document _doc;
        
        public bool IsPlaceRequested { get; private set; }
        public FamilySymbol? SelectedSymbol => (HangerFamilyCombo.SelectedItem as FamilySymbolWrapper)?.Symbol;
        public string SelectedScope { get; private set; } = "Selected"; // "Selected", "View", "Project"
        public double StartOffset { get; private set; } = 300;
        public double EndOffset { get; private set; } = 300;
        public double VerticalOffset { get; private set; } = 0;
        public double AdditionalRotationAngle { get; private set; } = 0; // in radians

        public ObservableCollection<HangerSpacingRule> SpacingRules { get; set; } = new ObservableCollection<HangerSpacingRule>();

        public HangerPlacementWindow(Document doc, List<FamilySymbol> symbols, bool hasSelection)
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

            RulesDataGrid.ItemsSource = SpacingRules;

            // Load default config
            LoadDefaultConfig();
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            SpacingRules.Add(new HangerSpacingRule { MaxWidth = 600, Spacing = 1500 });
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesDataGrid.SelectedItem is HangerSpacingRule selectedRule)
            {
                SpacingRules.Remove(selectedRule);
            }
        }

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            var config = new HangerConfig
            {
                StartOffset = double.Parse(StartOffsetTextBox.Text),
                EndOffset = double.Parse(EndOffsetTextBox.Text),
                VerticalOffset = double.Parse(VerticalOffsetTextBox.Text),
                RotationIndex = RotationCombo.SelectedIndex,
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2),
                SelectedFamilyName = (HangerFamilyCombo.SelectedItem as FamilySymbolWrapper)?.DisplayName ?? string.Empty,
                Rules = SpacingRules.ToList()
            };

            SettingsManager.SaveConfig("hanger_placement_config.json", config);
            MessageBox.Show(this, "Конфигурация креплений сохранена!", "GMDC Hanger Placement", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var loaded = SettingsManager.LoadConfig("hanger_placement_config.json", GetDefaultHangerConfig());
            ApplyConfig(loaded);
            MessageBox.Show(this, "Конфигурация креплений загружена!", "GMDC Hanger Placement", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadDefaultConfig()
        {
            var loaded = SettingsManager.LoadConfig("hanger_placement_config.json", GetDefaultHangerConfig());
            ApplyConfig(loaded);
        }

        private HangerConfig GetDefaultHangerConfig()
        {
            return new HangerConfig
            {
                StartOffset = 300,
                EndOffset = 300,
                VerticalOffset = 0,
                RotationIndex = 0,
                ScopeIndex = 0,
                SelectedFamilyName = string.Empty,
                Rules = new List<HangerSpacingRule>
                {
                    new HangerSpacingRule { MaxWidth = 200, Spacing = 2000 },
                    new HangerSpacingRule { MaxWidth = 400, Spacing = 1500 },
                    new HangerSpacingRule { MaxWidth = 10000, Spacing = 1200 }
                }
            };
        }

        private void ApplyConfig(HangerConfig config)
        {
            StartOffsetTextBox.Text = config.StartOffset.ToString();
            EndOffsetTextBox.Text = config.EndOffset.ToString();
            VerticalOffsetTextBox.Text = config.VerticalOffset.ToString();
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

            // Find family symbol match
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

            SpacingRules.Clear();
            foreach (var r in config.Rules)
            {
                SpacingRules.Add(r);
            }
        }

        private bool ValidateInputs()
        {
            if (HangerFamilyCombo.SelectedItem == null)
            {
                MessageBox.Show(this, "Пожалуйста, выберите семейство крепления!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!double.TryParse(StartOffsetTextBox.Text, out double _) ||
                !double.TryParse(EndOffsetTextBox.Text, out double _) ||
                !double.TryParse(VerticalOffsetTextBox.Text, out double _))
            {
                MessageBox.Show(this, "Введены некорректные числовые значения отступов!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!SpacingRules.Any())
            {
                MessageBox.Show(this, "Таблица правил шага расстановки не должна быть пустой!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            StartOffset = double.Parse(StartOffsetTextBox.Text);
            EndOffset = double.Parse(EndOffsetTextBox.Text);
            VerticalOffset = double.Parse(VerticalOffsetTextBox.Text);

            // Angle in radians (0, 90, 180, 270)
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

    public class FamilySymbolWrapper
    {
        public string DisplayName => $"{Symbol.Family.Name}: {Symbol.Name}";
        public FamilySymbol Symbol { get; set; } = null!;
    }

    public class HangerSpacingRule : INotifyPropertyChanged
    {
        private double _maxWidth;
        private double _spacing;

        public double MaxWidth
        {
            get => _maxWidth;
            set { _maxWidth = value; OnPropertyChanged(nameof(MaxWidth)); }
        }

        public double Spacing
        {
            get => _spacing;
            set { _spacing = value; OnPropertyChanged(nameof(Spacing)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class HangerConfig
    {
        public double StartOffset { get; set; } = 300;
        public double EndOffset { get; set; } = 300;
        public double VerticalOffset { get; set; } = 0;
        public int RotationIndex { get; set; } = 0;
        public int ScopeIndex { get; set; } = 0;
        public string SelectedFamilyName { get; set; } = string.Empty;
        public List<HangerSpacingRule> Rules { get; set; } = new List<HangerSpacingRule>();
    }
}
