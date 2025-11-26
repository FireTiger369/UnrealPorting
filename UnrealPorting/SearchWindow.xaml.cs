using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnrealPorting
{
    public partial class SearchWindow : Window
    {
        private readonly List<string> _allFiles;
        private readonly System.Timers.Timer _timer;

        public string? SelectedPath { get; private set; }
        public static string LastSearchText = "";

        public SearchWindow(List<string> filePaths)
        {
            InitializeComponent();

            _allFiles = filePaths;

            _timer = new System.Timers.Timer(150);
            _timer.AutoReset = false;
            _timer.Elapsed += DoSearch;

            PlaceholderText.Visibility =
                string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility =
                string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            _timer.Stop();
            _timer.Start();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = LastSearchText;
            PlaceholderText.Visibility =
                string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            LastSearchText = SearchBox.Text;
        }
        private void DoSearch(object? sender, ElapsedEventArgs e)
        {
            string query = "";

            Dispatcher.Invoke(() =>
            {
                query = SearchBox.Text.Trim();
            });

            if (string.IsNullOrWhiteSpace(query))
            {
                Dispatcher.Invoke(() => ResultsList.Items.Clear());
                return;
            }

            var results = _allFiles
                .Where(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .ToList();

            Dispatcher.Invoke(() =>
            {
                ResultsList.Items.Clear();
                foreach (var r in results)
                    ResultsList.Items.Add(r);
            });
        }

        public event Action<string>? AssetSelected;

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is string path)
            {
                AssetSelected?.Invoke(path);   // Notify MainWindow
            }
        }
        private void SearchBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (ResultsList.Items.Count == 0) return;

            if (e.Key == Key.Down)
            {
                ResultsList.SelectedIndex =
                    Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            else if (e.Key == Key.Up)
            {
                ResultsList.SelectedIndex =
                    Math.Max(ResultsList.SelectedIndex - 1, 0);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            else if (e.Key == Key.Enter)
            {
                if (ResultsList.SelectedItem is string path)
                    AssetSelected?.Invoke(path);
            }
        }
    }
}
