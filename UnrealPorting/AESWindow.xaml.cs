using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UnrealPorting
{
    public partial class AESWindow : Window
    {
        private readonly string _paksDir;
        private readonly Dictionary<string, string> _loadedKeys = new();
        public Dictionary<string, string> ResolvedKeys { get; private set; } = new();
        public string TempKeysFile { get; private set; } = string.Empty;

        public AESWindow(string paksDir)
        {
            InitializeComponent();
            _paksDir = paksDir;
            LoadAESList();
        }
        public Dictionary<Guid, string> GuidKeys { get; private set; }= new Dictionary<Guid, string>();

        public Dictionary<string, string> FileKeys { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);


        private void LoadAESList()
        {
            AESListPanel.Children.Clear();

            if (!Directory.Exists(_paksDir))
            {
                MessageBox.Show("PAKs directory not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var allUtocs = Directory.GetFiles(_paksDir, "*.utoc", SearchOption.TopDirectoryOnly);

            var filtered = allUtocs
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    if (name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (name.StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Contains(".o.utoc", StringComparison.OrdinalIgnoreCase)) return false;
                        if (name.Contains("optional", StringComparison.OrdinalIgnoreCase)) return false;
                        if (name.Contains("_Unlock_P", StringComparison.OrdinalIgnoreCase)) return false;

                        string numPart = new string(name.Skip("pakchunk".Length).TakeWhile(char.IsDigit).ToArray());
                        if (int.TryParse(numPart, out int n) && n >= 1000 && n <= 1099)
                            return true;
                    }

                    return false;
                })
                .OrderBy(f => f)
                .ToList();

            var profile = App.SelectedProfile;
            _loadedKeys.Clear();

            if (profile != null)
            {
                foreach (var kv in profile.AesFileKeys)
                    _loadedKeys[kv.Key] = kv.Value;

                foreach (var kv in profile.AesGuidKeys)
                    _loadedKeys[kv.Key] = kv.Value;
            }

            foreach (var file in filtered)
            {
                string fileName = Path.GetFileName(file);

                // 🔵 CONTAINER (round, shaded, themed)
                var container = new Border
                {
                    Style = (Style)FindResource("AESItemContainer")
                };

                // Inner layout for label + textbox
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Filename label
                var nameLabel = new TextBlock
                {
                    Text = fileName,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14
                };

                // Key textbox
                var keyBox = new TextBox
                {
                    Width = 420,
                    Text = _loadedKeys.ContainsKey(fileName) ? _loadedKeys[fileName] : "",
                    Style = (Style)FindResource("AESInputBox")
                };

                Grid.SetColumn(nameLabel, 0);
                Grid.SetColumn(keyBox, 1);

                rowGrid.Children.Add(nameLabel);
                rowGrid.Children.Add(keyBox);

                // Put row into container
                container.Child = rowGrid;

                // Add to stack panel
                AESListPanel.Children.Add(container);
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ResolvedKeys.Clear();
            GuidKeys.Clear();
            FileKeys.Clear();

            foreach (Border container in AESListPanel.Children)
            {
                if (container.Child is not Grid rowGrid)
                    continue;

                if (rowGrid.Children.Count < 2)
                    continue;

                var nameBlock = rowGrid.Children[0] as TextBlock;
                var keyBox = rowGrid.Children[1] as TextBox;

                if (nameBlock == null || keyBox == null)
                    continue;

                string fileName = nameBlock.Text.Trim();
                string keyText = keyBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(keyText))
                    continue;

                if (!keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    keyText = "0x" + keyText;

                ResolvedKeys[fileName] = keyText;

                string hex = keyText.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();

                if (fileName.Length == 32 && fileName.All(Uri.IsHexDigit))
                {
                    GuidKeys[new Guid(fileName)] = hex;
                }
                else
                {
                    FileKeys[fileName] = hex;
                }
            }

            DialogResult = true;
            Close();
        }


        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
