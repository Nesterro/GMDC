using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GMDC.Helpers
{
    public static class WpfThemeHelper
    {
        public static void ForceDarkTheme(DependencyObject obj)
        {
            if (obj == null) return;

            var darkBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30)); // #1E1E1E
            var borderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)); // #3F3F46

            if (obj is TextBox tb)
            {
                tb.Background = darkBrush;
                tb.Foreground = Brushes.White;
                tb.CaretBrush = Brushes.White;
                tb.BorderThickness = new Thickness(1);
                tb.BorderBrush = borderBrush;
                tb.VerticalContentAlignment = VerticalAlignment.Center;
                
                // Keep colors intact during user interactions
                tb.GotFocus += (s, e) => { tb.Background = darkBrush; tb.Foreground = Brushes.White; };
                tb.LostFocus += (s, e) => { tb.Background = darkBrush; tb.Foreground = Brushes.White; };
                tb.TextChanged += (s, e) => { tb.Background = darkBrush; tb.Foreground = Brushes.White; };
            }
            else if (obj is ToggleButton toggleBtn)
            {
                toggleBtn.Background = darkBrush;
                toggleBtn.Foreground = Brushes.White;
                toggleBtn.BorderBrush = borderBrush;
            }
            else if (obj is Border border)
            {
                // We only style borders that look like they belong to editable ComboBox textboxes,
                // to avoid overriding custom layouts/cards.
                if (border.TemplatedParent is ComboBox)
                {
                    border.Background = darkBrush;
                    border.BorderBrush = borderBrush;
                }
            }
            else if (obj is Control ctrl && !(ctrl is ComboBox) && !(ctrl is ListBox) && !(ctrl is DataGrid) && !(ctrl is Button))
            {
                ctrl.Background = darkBrush;
                ctrl.Foreground = Brushes.White;
                ctrl.BorderBrush = borderBrush;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                ForceDarkTheme(VisualTreeHelper.GetChild(obj, i));
            }
        }
    }
}
