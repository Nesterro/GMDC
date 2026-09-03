using System;
using System.Windows;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class CableRouteWindow : Window
    {
        // ──────────────────────────────────────────────────────────────
        // Result properties (read by the command after ShowDialog)
        // ──────────────────────────────────────────────────────────────
        public bool   IsRunRequested    { get; private set; }

        public string CableLevel        => CableLevelCombo.Text?.Trim() ?? "Силовой";
        public string CableMark         => CableMarkBox.Text?.Trim()    ?? "W1";
        public string CableName         => CableNameBox.Text?.Trim()    ?? "";
        public string NameParam         => NameParamCombo.Text?.Trim()  ?? "ADSK_Наименование";
        public string LengthParam       => LengthParamCombo.Text?.Trim() ?? "ADSK_Количество";
        public string ShapeMark         => ShapeMarkCombo.Text?.Trim()  ?? "Кабель";

        public bool   RepickPoints      => RepickPointsCheck.IsChecked == true;
        public bool   RepickTrays       => RepickTraysCheck.IsChecked == true;

        public int    RowIndex
        {
            get
            {
                // ComboBoxItem tags: -2,-1,0,+1,+2 → index maps to (-2 + index)
                int idx = RowCombo.SelectedIndex >= 0 ? RowCombo.SelectedIndex : 2;
                return idx - 2; // 0→-2, 1→-1, 2→0, 3→+1, 4→+2
            }
        }

        public double RowStepMm
        {
            get
            {
                if (double.TryParse(RowStepBox.Text, out double v) && v > 0) return v;
                return 50.0;
            }
        }

        public double CableSizeMm
        {
            get
            {
                if (double.TryParse(CableSizeBox.Text, out double v) && v > 0) return v;
                return 15.0;
            }
        }

        public double ReserveCoeff
        {
            get
            {
                if (double.TryParse(ReserveTextBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    return Math.Round(val, 2);
                }
                return 1.10;
            }
        }

        public bool   RoundLength   => RoundCheck.IsChecked == true;

        public bool UseGenericModel
        {
            get
            {
                // "Обобщенные модели" is index 0
                return CategoryCombo.SelectedIndex <= 0;
            }
        }

        public void EnableEditMode()
        {
            EditModeCard.Visibility = Visibility.Visible;
        }

        public void LoadFromData(string level, string mark, string name, string nameParam, string lengthParam, string shapeMark, int rowIndex, double rowStep, double size, double reserve, bool round, bool useGenericModel)
        {
            CableLevelCombo.Text = level;
            CableMarkBox.Text = mark;
            CableNameBox.Text = name;
            NameParamCombo.Text = nameParam;
            LengthParamCombo.Text = lengthParam;
            ShapeMarkCombo.Text = shapeMark;

            int rowIdx = Math.Max(0, Math.Min(4, rowIndex + 2));
            RowCombo.SelectedIndex = rowIdx;

            RowStepBox.Text = rowStep.ToString("0");
            CableSizeBox.Text = size.ToString("0");
            ReserveTextBox.Text = reserve.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            RoundCheck.IsChecked = round;

            CategoryCombo.SelectedIndex = useGenericModel ? 0 : 1;
        }

        // ──────────────────────────────────────────────────────────────
        // Config persistence
        // ──────────────────────────────────────────────────────────────
        private const string ConfigFile = "cable_route_config.json";

        public CableRouteWindow()
        {
            InitializeComponent();
            var cfg = SettingsManager.LoadConfig(ConfigFile, new CableRouteConfig());
            ApplyConfig(cfg);

            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    WpfThemeHelper.ForceDarkTheme(this);
                }));
            };
        }

        private void ApplyConfig(CableRouteConfig cfg)
        {
            CableLevelCombo.Text    = cfg.CableLevel;
            CableMarkBox.Text       = cfg.CableMark;
            CableNameBox.Text       = cfg.CableName;
            NameParamCombo.Text     = cfg.NameParam ?? "ADSK_Наименование";
            LengthParamCombo.Text   = cfg.LengthParam;
            ShapeMarkCombo.Text     = cfg.ShapeMark;

            // Row index: stored as -2..+2, ComboBox index is row+2
            int rowIdx = Math.Max(0, Math.Min(4, cfg.RowIndex + 2));
            RowCombo.SelectedIndex  = rowIdx;

            RowStepBox.Text         = cfg.RowStepMm.ToString("0");
            CableSizeBox.Text       = cfg.CableSizeMm.ToString("0");
            ReserveTextBox.Text     = cfg.ReserveCoeff.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            RoundCheck.IsChecked    = cfg.RoundLength;

            CategoryCombo.SelectedIndex = cfg.UseGenericModel ? 0 : 1;
        }

        private CableRouteConfig BuildConfig() => new CableRouteConfig
        {
            CableLevel      = CableLevel,
            CableMark       = CableMark,
            CableName       = CableName,
            NameParam       = NameParam,
            LengthParam     = LengthParam,
            ShapeMark       = ShapeMark,
            RowIndex        = RowIndex,
            RowStepMm       = RowStepMm,
            CableSizeMm     = CableSizeMm,
            ReserveCoeff    = ReserveCoeff,
            RoundLength     = RoundLength,
            UseGenericModel = UseGenericModel
        };

        // ──────────────────────────────────────────────────────────────
        // Event handlers
        // ──────────────────────────────────────────────────────────────

        private bool ValidateInputs()
        {
            if (!double.TryParse(ReserveTextBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double res) || res < 1.0 || res > 3.0)
            {
                MessageBox.Show(this, "Коэффициент запаса должен быть числовым значением от 1.0 до 3.0!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            SettingsManager.SaveConfig(ConfigFile, BuildConfig());
            IsRunRequested = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            IsRunRequested = false;
            DialogResult = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Config DTO
    // ──────────────────────────────────────────────────────────────────
    public class CableRouteConfig
    {
        public string CableLevel      { get; set; } = "Силовой";
        public string CableMark       { get; set; } = "W1";
        public string CableName       { get; set; } = "ВВГнг(А)-FRLS 3х2.5";
        public string NameParam       { get; set; } = "ADSK_Наименование";
        public string LengthParam     { get; set; } = "ADSK_Количество";
        public string ShapeMark       { get; set; } = "Кабель";
        public int    RowIndex        { get; set; } = 0;   // -2..+2
        public double RowStepMm       { get; set; } = 50.0;
        public double CableSizeMm     { get; set; } = 15.0;
        public double ReserveCoeff    { get; set; } = 1.10;
        public bool   RoundLength     { get; set; } = true;
        public bool   UseGenericModel { get; set; } = true;
    }
}
