using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace UnrealPorting
{
    public class JsonLineControl : TextBlock
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(JsonLineControl),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public JsonLineControl()
        {
            // Ensure clicking anywhere selects the full ListBox row
            this.MouseLeftButtonDown += (s, e) =>
            {
                var parent = FindParent<ListBoxItem>(this);
                if (parent != null)
                    parent.IsSelected = true;
            };
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (JsonLineControl)d;
            ctrl.Inlines.Clear();

            var text = e.NewValue as string ?? string.Empty;

            // Search for the first /Game/ path
            int idx = text.IndexOf("/Game/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                ctrl.Inlines.Add(new Run(text));
                return;
            }

            // Pre-path text
            if (idx > 0)
                ctrl.Inlines.Add(new Run(text[..idx]));

            // Extract path token
            int end = idx;
            while (end < text.Length &&
                   !char.IsWhiteSpace(text[end]) &&
                   text[end] != '"' &&
                   text[end] != ',' &&
                   text[end] != '}')
            {
                end++;
            }

            string path = text.Substring(idx, end - idx);

            // Get accent color from theme
            Brush accentBrush;
            try
            {
                accentBrush = (Brush)Application.Current.FindResource("BrushAccent");
            }
            catch
            {
                accentBrush = Brushes.DeepSkyBlue;
            }

            // ⭐ Make the path clickable (Hyperlink)
            var hyperlink = new Hyperlink(new Run(path))
            {
                Foreground = accentBrush,
                TextDecorations = System.Windows.TextDecorations.Underline
            };

            hyperlink.Click += (s, ev) =>
            {
                // Call NavigateToAsset(path)
                var win = Application.Current.MainWindow as MainWindow;
                win?.NavigateToAsset(path);
            };

            ctrl.Inlines.Add(hyperlink);

            // After-path text
            if (end < text.Length)
                ctrl.Inlines.Add(new Run(text[end..]));
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null && parent is not T)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as T;
        }
    }
}
