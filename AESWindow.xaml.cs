using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UnrealPorting2;

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

            // ✅ Filter only relevant utocs
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

            Console.WriteLine($"[AES] Displaying {filtered.Count} filtered .utoc files.");

            // ✅ Load existing aes_keys.txt
            var profile = App.SelectedProfile;

            _loadedKeys.Clear();

            if (profile != null)
            {
                // Load filename keys
                foreach (var kv in profile.AesFileKeys)
                    _loadedKeys[kv.Key] = kv.Value;

                // Load GUID keys (convert GUID → hex)
                foreach (var kv in profile.AesGuidKeys)
                    _loadedKeys[kv.Key] = kv.Value;
            }

            Console.WriteLine($"[AES] Loaded {_loadedKeys.Count} key(s) from active profile.");

            // ✅ Build UI rows
            foreach (var file in filtered)
            {
                var fileName = Path.GetFileName(file);

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 5, 0, 5)
                };

                var nameLabel = new TextBlock
                {
                    Text = fileName,
                    Width = 380,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var keyBox = new TextBox
                {
                    Width = 450,
                    Text = _loadedKeys.ContainsKey(fileName) ? _loadedKeys[fileName] : "",
                    Background = System.Windows.Media.Brushes.DimGray,
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderBrush = System.Windows.Media.Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 13,
                    Margin = new Thickness(10, 0, 0, 0)
                };

                row.Children.Add(nameLabel);
                row.Children.Add(keyBox);
                AESListPanel.Children.Add(row);
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ResolvedKeys.Clear();
            GuidKeys.Clear();
            FileKeys.Clear();

            foreach (StackPanel row in AESListPanel.Children)
            {
                if (row.Children.Count < 2) continue;

                var nameBlock = row.Children[0] as TextBlock;
                var keyBox = row.Children[1] as TextBox;

                if (nameBlock == null || keyBox == null)
                    continue;

                string fileName = nameBlock.Text.Trim();
                string keyText = keyBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(keyText))
                    continue;

                if (!keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    keyText = "0x" + keyText;

                // Store raw
                ResolvedKeys[fileName] = keyText;

                // 🔥 Sort keys into GUID vs Filename
                string hex = keyText.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();

                if (fileName.Length == 32 && fileName.All(Uri.IsHexDigit))
                {
                    // GUID-style (pak guid)
                    GuidKeys[new Guid(fileName)] = hex;
                }
                else
                {
                    // Filename-based (pakchunk1007-WindowsClient.utoc)
                    FileKeys[fileName] = hex;
                }
            }

            Console.WriteLine($"[AES] Received {ResolvedKeys.Count} AES key(s) from AES window.");
            Console.WriteLine($"[AES] → GUID Keys: {GuidKeys.Count}, File Keys: {FileKeys.Count}");

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
