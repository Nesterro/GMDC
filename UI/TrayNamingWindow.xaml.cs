using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using GMDC.Helpers;

namespace GMDC.UI
{
    public partial class TrayNamingWindow : Window
    {
        private readonly Document _doc;
        private Element? _sampleElement;
        private readonly List<Element> _selectedElements;
        private readonly List<Element> _viewTrays;
        private readonly List<Element> _allTrays;
        private bool _ready;

        public bool IsRunRequested { get; private set; }
        public string SelectedScope { get; private set; } = "Selected"; // "Selected", "View", "Project"
        public string TargetParameter { get; private set; } = string.Empty;
        public string NameTemplate { get; private set; } = string.Empty;
        public bool WriteLength { get; private set; }
        public string LengthSourceParam { get; private set; } = string.Empty;
        public string LengthTargetParam { get; private set; } = string.Empty;

        public TrayNamingWindow(Document doc, List<Element> selectedElements, List<Element> viewTrays, List<Element> allTrays)
        {
            InitializeComponent();
            _doc = doc;
            _selectedElements = selectedElements;
            _viewTrays = viewTrays;
            _allTrays = allTrays;
            ScopeSelectedRadio.IsEnabled = selectedElements.Count > 0;
            ScopeSelectedRadio.IsChecked = selectedElements.Count > 0;
            ScopeViewRadio.IsChecked = selectedElements.Count == 0;
            Loaded += (s, e) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => WpfThemeHelper.ForceDarkTheme(this)));
            _ready = true;
            RefreshParameters();
            LoadDefaultTemplate();
            // An explicit selection takes precedence over a previously saved project scope.
            if (selectedElements.Count > 0) ScopeSelectedRadio.IsChecked = true;
        }

        private void Scope_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) RefreshParameters();
        }

        private void RefreshParameters()
        {
            var elements = ScopeSelectedRadio.IsChecked == true ? _selectedElements
                : ScopeViewRadio.IsChecked == true ? _viewTrays : _allTrays;
            _sampleElement = elements.FirstOrDefault();
            var source = new HashSet<string>();
            var target = new HashSet<string>();
            var lengthSource = new HashSet<string>();
            var lengthTarget = new HashSet<string>();
            var visitedTypes = new HashSet<ElementId>();
            foreach (var element in elements)
            {
                // Inspect every instance: instance-bound parameters may differ even within one family.
                foreach (Parameter p in element.Parameters)
                {
                    source.Add(p.Definition.Name);
                    if (!p.IsReadOnly && p.StorageType == StorageType.String) target.Add(p.Definition.Name);
                    if (IsLength(p)) lengthSource.Add(p.Definition.Name);
                    if (!p.IsReadOnly && (p.StorageType == StorageType.String
                        || p.Definition.GetDataType() == SpecTypeId.Length
                        || p.Definition.GetDataType() == SpecTypeId.Number
                        || p.Definition.GetDataType() == SpecTypeId.Int.Integer)) lengthTarget.Add(p.Definition.Name);
                }
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId && visitedTypes.Add(typeId))
                {
                    var type = _doc.GetElement(typeId);
                    if (type == null) continue;
                    foreach (Parameter p in type.Parameters)
                    {
                        source.Add(p.Definition.Name);
                        if (IsLength(p)) lengthSource.Add(p.Definition.Name);
                    }
                }
            }
            if (elements.Count > 0)
            {
                source.UnionWith(new[] { "Type Name", "Family Name" });
                if (elements.Any(el => el is Autodesk.Revit.DB.Electrical.CableTray))
                    source.UnionWith(new[] { "Width", "Height", "Size" });
            }
            ParametersListBox.ItemsSource = source.OrderBy(x => x).ToList();
            SetChoices(TargetParamCombo, target, "ADSK_Наименование");
            SetChoices(LengthSourceCombo, lengthSource, "Длина");
            SetChoices(LengthTargetCombo, lengthTarget, "ADSK_Количество");
            UpdatePreview();
        }

        private static bool IsLength(Parameter p) => p.StorageType == StorageType.Double
            && p.Definition.GetDataType() == SpecTypeId.Length;

        private static void SetChoices(ComboBox combo, IEnumerable<string> values, string defaultText)
        {
            string current = combo.Text;
            combo.ItemsSource = values.OrderBy(x => x).ToList();
            combo.Text = string.IsNullOrEmpty(current) ? defaultText : current;
        }
        private void ParametersListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ParametersListBox.SelectedItem is string paramName)
            {
                string token = $"{{{paramName}}}";
                int caretIndex = TemplateTextBox.CaretIndex;
                TemplateTextBox.Text = TemplateTextBox.Text.Insert(caretIndex, token);
                TemplateTextBox.CaretIndex = caretIndex + token.Length;
                TemplateTextBox.Focus();
            }
        }

        private void TemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (PreviewTextBlock == null) return;

            string template = TemplateTextBox.Text;
            if (string.IsNullOrWhiteSpace(template))
            {
                PreviewTextBlock.Text = "[Введите шаблон]";
                return;
            }

            if (_sampleElement == null)
            {
                PreviewTextBlock.Text = "[Нет элементов для предпросмотра]";
                return;
            }

            string preview = ResolveTemplate(template, _sampleElement);
            PreviewTextBlock.Text = preview;
        }

        public static string ResolveTemplate(string template, Element element)
        {
            string result = template;
            int startIndex = 0;

            while ((startIndex = result.IndexOf('{', startIndex)) != -1)
            {
                int endIndex = result.IndexOf('}', startIndex);
                if (endIndex == -1) break;

                string token = result.Substring(startIndex + 1, endIndex - startIndex - 1);
                string value = RevitUtils.GetParameterValueString(element, token);

                result = result.Remove(startIndex, endIndex - startIndex + 1);
                result = result.Insert(startIndex, value);

                startIndex += value.Length;
            }

            return result;
        }

        private void SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            var config = new NamingConfig
            {
                Template = TemplateTextBox.Text,
                TargetParameter = TargetParamCombo.Text,
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2),
                WriteLength = WriteLengthCheck.IsChecked == true,
                LengthSourceParam = LengthSourceCombo.Text,
                LengthTargetParam = LengthTargetCombo.Text
            };
            SettingsManager.SaveConfig("tray_naming_config.json", config);
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDC");
            string filePath = Path.Combine(appData, "tray_naming_config.json");
            MessageBox.Show(this, $"Конфигурация успешно сохранена в:\n{filePath}", "GMDC Cable Tray Naming", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadTemplate_Click(object sender, RoutedEventArgs e)
        {
            var loaded = SettingsManager.LoadConfig("tray_naming_config.json", new NamingConfig());
            ApplyConfig(loaded);
            MessageBox.Show(this, "Шаблон успешно загружен!", "GMDC Cable Tray Naming", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadDefaultTemplate()
        {
            bool familiesOnly = _selectedElements.Count > 0
                && _selectedElements.All(el => el is FamilyInstance);
            var loaded = SettingsManager.LoadConfig("tray_naming_config.json", new NamingConfig
            {
                Template = familiesOnly ? "{Family Name} - {Type Name}" : "{Type Name} - {Width}x{Height}",
                WriteLength = !familiesOnly
            });
            ApplyConfig(loaded);
        }

        private void ApplyConfig(NamingConfig config)
        {
            TemplateTextBox.Text = config.Template;

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

            if (string.IsNullOrEmpty(config.TargetParameter))
            {
                config.TargetParameter = "ADSK_Наименование";
            }

            if (!string.IsNullOrEmpty(config.TargetParameter))
            {
                bool found = false;
                foreach (var item in TargetParamCombo.Items)
                {
                    if (item.ToString() == config.TargetParameter)
                    {
                        TargetParamCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    TargetParamCombo.Text = config.TargetParameter;
                }
            }

            WriteLengthCheck.IsChecked = config.WriteLength;
            if (LengthSettingsGrid != null)
            {
                LengthSettingsGrid.IsEnabled = config.WriteLength;
            }

            if (!string.IsNullOrEmpty(config.LengthSourceParam))
            {
                bool found = false;
                foreach (var item in LengthSourceCombo.Items)
                {
                    if (item.ToString() == config.LengthSourceParam)
                    {
                        LengthSourceCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found) LengthSourceCombo.Text = config.LengthSourceParam;
            }

            if (!string.IsNullOrEmpty(config.LengthTargetParam))
            {
                bool found = false;
                foreach (var item in LengthTargetCombo.Items)
                {
                    if (item.ToString() == config.LengthTargetParam)
                    {
                        LengthTargetCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
                if (!found) LengthTargetCombo.Text = config.LengthTargetParam;
            }

            UpdatePreview();
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TemplateTextBox.Text))
            {
                MessageBox.Show(this, "Пожалуйста, введите шаблон наименования!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetParam = TargetParamCombo.Text.Trim();
            if (string.IsNullOrEmpty(targetParam))
            {
                MessageBox.Show(this, "Пожалуйста, выберите или введите целевой параметр для записи!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WriteLength = WriteLengthCheck.IsChecked == true;
            LengthSourceParam = LengthSourceCombo.Text.Trim();
            LengthTargetParam = LengthTargetCombo.Text.Trim();

            if (WriteLength)
            {
                if (string.Equals(targetParam, LengthTargetParam, StringComparison.Ordinal))
                {
                    MessageBox.Show(this, "Для наименования и длины выберите разные целевые параметры.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(LengthSourceParam))
                {
                    MessageBox.Show(this, "Пожалуйста, выберите или введите параметр длины (источник)!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(LengthTargetParam))
                {
                    MessageBox.Show(this, "Пожалуйста, выберите или введите целевой параметр для записи длины!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            NameTemplate = TemplateTextBox.Text;
            TargetParameter = targetParam;

            if (ScopeSelectedRadio.IsChecked == true) SelectedScope = "Selected";
            else if (ScopeViewRadio.IsChecked == true) SelectedScope = "View";
            else SelectedScope = "Project";

            IsRunRequested = true;
            DialogResult = true;
            Close();
        }

        private void WriteLengthCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (LengthSettingsGrid != null)
            {
                LengthSettingsGrid.IsEnabled = WriteLengthCheck.IsChecked == true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class NamingConfig
    {
        public string Template { get; set; } = "{Type Name} - {Width}x{Height}";
        public string TargetParameter { get; set; } = "ADSK_Наименование";
        public int ScopeIndex { get; set; } = 0; // 0: Selected, 1: View, 2: Project
        public bool WriteLength { get; set; } = true;
        public string LengthSourceParam { get; set; } = "Длина";
        public string LengthTargetParam { get; set; } = "ADSK_Количество";
    }
}
