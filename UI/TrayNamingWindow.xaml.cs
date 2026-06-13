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
        private readonly Element? _sampleElement;

        public bool IsRunRequested { get; private set; }
        public string SelectedScope { get; private set; } = "Selected"; // "Selected", "View", "Project"
        public string TargetParameter { get; private set; } = string.Empty;
        public string NameTemplate { get; private set; } = string.Empty;
        public bool WriteLength { get; private set; }
        public string LengthSourceParam { get; private set; } = string.Empty;
        public string LengthTargetParam { get; private set; } = string.Empty;

        public TrayNamingWindow(Document doc, List<Element> selectedTrays, List<Element> allTrays)
        {
            InitializeComponent();
            _doc = doc;

            // Set up scope selection states
            if (selectedTrays.Count == 0)
            {
                ScopeSelectedRadio.IsEnabled = false;
                ScopeViewRadio.IsChecked = true;
            }
            else
            {
                ScopeSelectedRadio.IsChecked = true;
            }

            // Find a sample element for live preview
            _sampleElement = selectedTrays.FirstOrDefault() ?? allTrays.FirstOrDefault();

            // Populate source parameters list (common cable tray parameters)
            List<string> sourceParams = new List<string> { "Type Name", "Width", "Height", "Size" };
            if (_sampleElement != null)
            {
                var uniqueParams = RevitUtils.GetNamingSourceParameters(_sampleElement);
                foreach (var p in uniqueParams)
                {
                    if (!sourceParams.Contains(p))
                    {
                        sourceParams.Add(p);
                    }
                }
            }
            ParametersListBox.ItemsSource = sourceParams.OrderBy(x => x).ToList();

            // Populate target parameter list (writable string parameters + common defaults)
            List<string> targetParams = new List<string> { "Comments", "Mark", "ADSK_Наименование", "ADSK_Имя", "Наименование", "Описание" };
            if (_sampleElement != null)
            {
                var writable = RevitUtils.GetWritableStringParameters(_sampleElement);
                foreach (var w in writable)
                {
                    if (!targetParams.Contains(w))
                    {
                        targetParams.Add(w);
                    }
                }
            }
            TargetParamCombo.ItemsSource = targetParams.OrderBy(x => x).ToList();
            if (TargetParamCombo.Items.Count > 0)
            {
                TargetParamCombo.SelectedIndex = 0;
            }

            // Populate source parameters for length
            List<string> sourceLengthParams = new List<string> { "Длина", "Length" };
            if (_sampleElement != null)
            {
                // Try to get localized name of system length parameter
                Parameter? pLen = _sampleElement.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (pLen != null)
                {
                    string sysName = pLen.Definition.Name;
                    if (!sourceLengthParams.Contains(sysName))
                    {
                        sourceLengthParams.Add(sysName);
                    }
                }

                // Add all other double parameters
                foreach (Parameter p in _sampleElement.Parameters)
                {
                    if (p.StorageType == StorageType.Double)
                    {
                        string name = p.Definition.Name;
                        if (!sourceLengthParams.Contains(name))
                        {
                            sourceLengthParams.Add(name);
                        }
                    }
                }
            }
            LengthSourceCombo.ItemsSource = sourceLengthParams.OrderBy(x => x).ToList();
            if (LengthSourceCombo.Items.Count > 0)
            {
                string preferred = "Длина";
                if (_sampleElement != null)
                {
                    Parameter? pLen = _sampleElement.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    if (pLen != null) preferred = pLen.Definition.Name;
                }
                int idx = sourceLengthParams.OrderBy(x => x).ToList().IndexOf(preferred);
                if (idx == -1) idx = sourceLengthParams.OrderBy(x => x).ToList().IndexOf("Length");
                LengthSourceCombo.SelectedIndex = idx != -1 ? idx : 0;
            }

            // Populate target parameters for length (writable parameters + defaults)
            List<string> targetLengthParams = new List<string> { "ADSK_Количество", "ADSK_Количество_Длина", "Comments", "Mark" };
            if (_sampleElement != null)
            {
                foreach (Parameter p in _sampleElement.Parameters)
                {
                    if (!p.IsReadOnly)
                    {
                        string name = p.Definition.Name;
                        if (!targetLengthParams.Contains(name))
                        {
                            targetLengthParams.Add(name);
                        }
                    }
                }
            }
            LengthTargetCombo.ItemsSource = targetLengthParams.OrderBy(x => x).ToList();
            if (LengthTargetCombo.Items.Count > 0)
            {
                int idx = targetLengthParams.OrderBy(x => x).ToList().IndexOf("ADSK_Количество");
                LengthTargetCombo.SelectedIndex = idx != -1 ? idx : 0;
            }

            // Style the internal controls of the ComboBoxes when they load
            RoutedEventHandler comboLoaded = (s, e) =>
            {
                if (s is DependencyObject depObj)
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        WpfThemeHelper.ForceDarkTheme(depObj);
                    }));
                }
            };
            TargetParamCombo.Loaded += comboLoaded;
            LengthSourceCombo.Loaded += comboLoaded;
            LengthTargetCombo.Loaded += comboLoaded;

            // Load default configuration
            LoadDefaultTemplate();
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
                PreviewTextBlock.Text = "[Нет лотков для предпросмотра]";
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
                TargetParameter = TargetParamCombo.SelectedItem?.ToString() ?? TargetParamCombo.Text,
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2),
                WriteLength = WriteLengthCheck.IsChecked == true,
                LengthSourceParam = LengthSourceCombo.SelectedItem?.ToString() ?? LengthSourceCombo.Text,
                LengthTargetParam = LengthTargetCombo.SelectedItem?.ToString() ?? LengthTargetCombo.Text
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
            var loaded = SettingsManager.LoadConfig("tray_naming_config.json", new NamingConfig
            {
                Template = "{Type Name} - {Width}x{Height}"
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
        public string TargetParameter { get; set; } = "Comments";
        public int ScopeIndex { get; set; } = 0; // 0: Selected, 1: View, 2: Project
        public bool WriteLength { get; set; } = false;
        public string LengthSourceParam { get; set; } = "Длина";
        public string LengthTargetParam { get; set; } = "ADSK_Количество";
    }
}
