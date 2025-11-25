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

        public SearchWindow(List<string> filePaths)
        {
            InitializeComponent();

            _allFiles = filePaths;

            _timer = new System.Timers.Timer(150);
            _timer.AutoReset = false;
            _timer.Elapsed += DoSearch;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _timer.Stop();
            _timer.Start();
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
    }
}
