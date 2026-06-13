using System;
using System.Collections.Generic;
using System.Linq;
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

            // Style the internal TextBox of the editable ComboBox when it loads to override default system theme colors
            TargetParamCombo.Loaded += (s, e) =>
            {
                var tb = FindVisualChild<TextBox>(TargetParamCombo);
                if (tb != null)
                {
                    var darkBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)); // #1E1E1E
                    tb.Background = darkBrush;
                    tb.Foreground = Brushes.White;
                    tb.CaretBrush = Brushes.White;
                    tb.BorderThickness = new Thickness(0);
                    tb.VerticalContentAlignment = VerticalAlignment.Center;
                    
                    // Force the background/foreground to stay dark on focus/input events
                    tb.GotFocus += (sender, args) =>
                    {
                        tb.Background = darkBrush;
                        tb.Foreground = Brushes.White;
                    };
                    tb.LostFocus += (sender, args) =>
                    {
                        tb.Background = darkBrush;
                        tb.Foreground = Brushes.White;
                    };
                    tb.TextChanged += (sender, args) =>
                    {
                        tb.Background = darkBrush;
                        tb.Foreground = Brushes.White;
                    };
                }
            };

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
                TargetParameter = TargetParamCombo.SelectedItem?.ToString() ?? "Comments",
                ScopeIndex = ScopeSelectedRadio.IsChecked == true ? 0 : (ScopeViewRadio.IsChecked == true ? 1 : 2)
            };
            SettingsManager.SaveConfig("tray_naming_config.json", config);
            MessageBox.Show(this, "Шаблон успешно сохранен!", "GMDC Cable Tray Naming", MessageBoxButton.OK, MessageBoxImage.Information);
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

            NameTemplate = TemplateTextBox.Text;
            TargetParameter = targetParam;
            
            if (ScopeSelectedRadio.IsChecked == true) SelectedScope = "Selected";
            else if (ScopeViewRadio.IsChecked == true) SelectedScope = "View";
            else SelectedScope = "Project";

            IsRunRequested = true;
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child != null)
                {
                    if (child is T tChild)
                    {
                        return tChild;
                    }
                    
                    T? childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null)
                    {
                        return childOfChild;
                    }
                }
            }
            return null;
        }
    }

    public class NamingConfig
    {
        public string Template { get; set; } = "{Type Name} - {Width}x{Height}";
        public string TargetParameter { get; set; } = "Comments";
        public int ScopeIndex { get; set; } = 0; // 0: Selected, 1: View, 2: Project
    }
}
