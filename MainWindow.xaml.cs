using CUE4Parse.Compression;
using CUE4Parse.MappingsProvider;
using Microsoft.WindowsAPICodePack.Dialogs;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using UnrealPorting.Helpers;
using UnrealPorting.Properties;
using UnrealPorting2;
namespace UnrealPorting
{
    public partial class MainWindow : Window
    {
        private string _gameDirectory;
        private Dictionary<string, string> _aesKeysDictionary = new();
        private AppPakReader? _pakReader;

        private readonly List<string> _globalFilePaths = new();

        // Maps paths → readers
        private readonly Dictionary<string, AppPakReader> _pathToReader =
            new(StringComparer.OrdinalIgnoreCase);

        // Persistent AES key file
        private readonly string _aesKeysFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aes_keys.txt");

        // Folder trie
        private FolderTrie? _folderTrie;
        private CancellationTokenSource? _loadCts;
        private const int MAX_FILES_PER_FOLDER = 3000;
        private string _oodleDllPath = "";

        public MainWindow()
        {
            Console.WriteLine("[DEBUG] MainWindow created — UI hooks active");

            InitializeComponent();
            App.ProfileChanged += OnProfileChanged;

            Console.WriteLine("[INFO] MainWindow initialized — waiting for game directory selection.");

            // Load saved AES keys
            try
            {
                if (File.Exists(_aesKeysFile))
                {
                    var lines = File.ReadAllLines(_aesKeysFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim())
                        .ToList();

                    _aesKeysDictionary = lines
                        .Select(l => l.Split('=', 2))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(
                            parts => parts[0].Trim(),
                            parts => parts[1].Trim(),
                            StringComparer.OrdinalIgnoreCase
                        );

                    Console.WriteLine($"[AES] Loaded {_aesKeysDictionary.Count} AES key(s) from {_aesKeysFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to load AES keys: {ex.Message}");
            }
        }

        #region Directory & AES Handling

        private void OpenAESWindow_Click(object sender, RoutedEventArgs e)
        {
            var profile = App.SelectedProfile;

            if (profile == null)
            {
                MessageBox.Show(
                    "Please select a game profile first.",
                    "No Profile Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            // Validate directory inside profile
            if (string.IsNullOrWhiteSpace(profile.Directory) ||
                !Directory.Exists(profile.Directory))
            {
                MessageBox.Show(
                    "The selected profile does not have a valid directory.\nPlease edit the profile or choose another.",
                    "Invalid Directory",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            // Resolve Paks in profile path
            string? paksDir = ResolvePaksDirectory(profile.Directory);
            if (paksDir == null)
            {
                MessageBox.Show(
                    "Could not locate a valid 'Paks' folder inside this game's directory.",
                    "Paks Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            // Open AES editor window
            var aesWindow = new AESWindow(paksDir);

            if (aesWindow.ShowDialog() == true)
            {
                profile.AesFileKeys = new Dictionary<string, string>(aesWindow.FileKeys);
                profile.AesGuidKeys = aesWindow.GuidKeys
                    .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

                GameProfileStore.Save();
                MessageBox.Show("AES keys saved to this profile.", "AES Saved");

                Console.WriteLine($"[PROFILE] Saved AES keys for profile '{profile.Name}'");

            }
        }

        private void ReloadGameWithProfile(GameProfile profile)
        {
            Console.WriteLine("[LOAD] Reloading game files using updated AES keys...");

            // Resolve actual Paks folder
            string? paksDir = ResolvePaksDirectory(profile.Directory);
            if (paksDir == null)
            {
                Console.WriteLine("[ERROR] Could not locate Paks directory for profile.");
                return;
            }

            // Initialize Oodle using the Paks directory
            EnsureOodleInitialized(paksDir);

            _pakReader?.Dispose();
            _pakReader = new AppPakReader(
                paksDir,
                profile.AesGuidKeys.ToDictionary(
                    kv => Guid.Parse(kv.Key), kv => kv.Value),
                profile.AesFileKeys,
                profile.MappingPath
            );

            _globalFilePaths.Clear();
            foreach (var path in _pakReader.EnumerateFilePaths())
                if (!string.IsNullOrWhiteSpace(path))
                    _globalFilePaths.Add(path);

            BuildFolderTreeUI();
        }

        private void BuildFolderTreeUI()
        {
            var interner = new StringInterner();
            _folderTrie = new FolderTrie(interner);

            foreach (var raw in _globalFilePaths)
            {
                if (raw.StartsWith("FortniteGame/", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
                {
                    _folderTrie.AddPath(raw);
                }
            }

            _folderTrie.Compact();

            Dispatcher.Invoke(() =>
            {
                GameFoldersTreeView.Items.Clear();

                var root = _folderTrie?.Root;
                if (root == null || root.Children.Count == 0)
                {
                    GameFoldersTreeView.Items.Add(new TreeViewItem
                    {
                        Header = "(No folders found)"
                    });
                    return;
                }

                foreach (var kv in root.Children)
                {
                    var item = new TreeViewItem { Header = kv.Key, Tag = kv.Key };
                    if (kv.Value.Children.Count > 0)
                        item.Items.Add(new TreeViewItem { Header = "Loading..." });

                    item.Expanded += FolderNode_Expanded;
                    GameFoldersTreeView.Items.Add(item);
                }
            });
        }



        #endregion

        #region Game Folder & Package Loading

        private void LoadAssetsForSelectedFolder(string folderPath)
        {
            folderPath = NormalizeFolderPath(folderPath);
            GamePackagesTreeView.Items.Clear();

            string prefix = folderPath.TrimEnd('/') + "/";

            // Count ONLY uasset + umap
            int assetCount = _globalFilePaths.Count(f =>
                f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                 f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            );

            // Rule: block only if TOO MANY ASSETS (not uexp/bin/etc.)
            if (assetCount > MAX_FILES_PER_FOLDER)
            {
                GamePackagesTreeView.Items.Add(new TreeViewItem
                {
                    Header = "(This folder contains too many assets. Select a deeper subfolder.)"
                });

                Console.WriteLine($"[SKIP] Folder '{folderPath}' has {assetCount} assets — blocked.");
                return;
            }

            // Load assets
            var assets = _globalFilePaths
                .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Distinct()
                .ToList();

            if (assets.Count == 0)
            {
                GamePackagesTreeView.Items.Add(new TreeViewItem { Header = "(No assets found)" });
            }
            else
            {
                foreach (var asset in assets)
                {
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };

                    var icon = new Image
                    {
                        Source = new BitmapImage(new Uri("pack://application:,,,/Icons/asset_icon.png")),
                        Width = 16,
                        Height = 16,
                        Margin = new Thickness(0, 0, 6, 0)
                    };

                    var label = new TextBlock
                    {
                        Text = asset,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.White
                    };

                    panel.Children.Add(icon);
                    panel.Children.Add(label);

                    var item = new TreeViewItem
                    {
                        Header = panel,
                        Tag = $"{folderPath}/{asset}",
                        ToolTip = asset      // <– prevents "StackPanel" text from showing anywhere
                    };

                    GamePackagesTreeView.Items.Add(item);
                }
            }

            Console.WriteLine($"[DEBUG] Folder '{folderPath}' loaded {assets.Count} assets.");
        }

        #endregion

        #region TreeView Building / Expansion

        private void PakNode_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem node || node.Items.Count > 0)
                return;

            if (node.Tag is AppPakReader reader)
            {
                node.Items.Clear();

                foreach (var entry in reader.EnumerateFilePaths())
                {
                    var child = new TreeViewItem
                    {
                        Header = entry,
                        Tag = entry
                    };

                    child.Items.Add(new TreeViewItem { Header = "Loading..." });
                    child.Expanded += FolderNode_Expanded;

                    node.Items.Add(child);
                }
            }
        }

        private async void FolderNode_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem node)
                return;

            e.Handled = true;

            if (node.Tag is not string path)
                return;

            if (node.Items.Count == 0)
                return;

            if (node.Items.Count == 1 &&
                node.Items[0] is TreeViewItem dummy &&
                (string)dummy.Header == "Loading...")
            {
                node.Items.Clear();
                await LoadSubfoldersAsync(node, path);
            }
        }

        private async Task LoadSubfoldersAsync(TreeViewItem parent, string path)
        {
            List<(string Name, string FullPath, bool HasChildren)> subfolders = new();

            await Task.Run(() =>
            {
                var folderNode = _folderTrie?.GetNode(path);
                if (folderNode == null)
                    return;

                foreach (var kv in folderNode.Children)
                {
                    string subName = kv.Key;
                    string subPath =
                        string.IsNullOrEmpty(path) ? subName : $"{path}/{subName}";
                    bool hasChildren = kv.Value.Children.Count > 0;

                    subfolders.Add((subName, subPath, hasChildren));
                }
            });

            foreach (var (name, fullPath, hasChildren) in subfolders
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var subItem = new TreeViewItem
                {
                    Header = name,
                    Tag = fullPath
                };

                if (hasChildren)
                    subItem.Items.Add(new TreeViewItem { Header = "Loading..." });

                subItem.Expanded += FolderNode_Expanded;

                parent.Items.Add(subItem);
            }
        }

        private void LoadArchivesListUI()
        {
            ArchivesTreeView.Items.Clear();

            if (_pakReader == null)
                return;

            var vfsList = _pakReader.Provider.MountedVfs;

            if (vfsList == null || vfsList.Count == 0)
            {
                ArchivesTreeView.Items.Add(new TreeViewItem
                {
                    Header = "(No archives mounted)"
                });
                return;
            }

            foreach (var vfs in vfsList.OrderBy(v => v.Name))
            {
                ArchivesTreeView.Items.Add(new TreeViewItem
                {
                    Header = vfs.Name,
                    Tag = vfs
                });
            }
        }

        private void OnProfileChanged(GameProfile? profile)
        {
            Console.WriteLine(profile != null
                ? $"[PROFILE] Switched to: {profile.Name}"
                : "[PROFILE] Cleared");

            // 1) Clear in-memory AES caches
            _aesKeysDictionary.Clear();

            _pakReader?.Dispose();
            _pakReader = null;

            // 3) Clear file lists + folder tree
            _globalFilePaths.Clear();
            _folderTrie = null;

            // 4) Clear UI panels
            ArchivesTreeView.Items.Clear();
            GameFoldersTreeView.Items.Clear();
            GamePackagesTreeView.Items.Clear();
            ShowSinglePaneText("");

            Console.WriteLine("[PROFILE] Reset AES, PakReader, UI state.");
        }



        #endregion

        #region Selection Events

        private void ArchivesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            PreviewManager.ShowFilePreviewAsync(
                ArchivesTreeView.SelectedItem as TreeViewItem,
                this,
                filePath => FindReaderForPath(filePath)
            );
        }

        private void GamePackagesTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            PreviewManager.ShowFilePreviewAsync(
                GamePackagesTreeView.SelectedItem as TreeViewItem,
                this,
                filePath => FindReaderForPath(filePath)
            );
        }

        private void GameFoldersTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem node)
                return;

            if (node.Tag is not string folderPath)
                return;

            folderPath = NormalizeFolderPath(folderPath);

            // 🟦 Always attempt to load assets for the selected folder
            LoadAssetsForSelectedFolder(folderPath);
        }

        private void GameFoldersTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GameFoldersTreeView.SelectedItem is not TreeViewItem node)
                return;

            string folderPath = node.Tag?.ToString();
            if (string.IsNullOrEmpty(folderPath))
                return;

            folderPath = NormalizeFolderPath(folderPath);

            // Switch to Packages tab
            var tabControl = FindParent<TabControl>(GameFoldersTreeView);
            if (tabControl != null)
                tabControl.SelectedIndex = 2;

            LoadAssetsForSelectedFolder(folderPath);
        }
        private void OpenSettingsWindow_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow();
            win.Owner = this;
            win.ShowDialog();
        }

        private void OpenDirectoryWindow_Click(object sender, RoutedEventArgs e)
        {
            var win = new DirectorySelectorWindow();
            win.Owner = this;
            win.ShowDialog();
        }

        private async void OpenSearchWindow_Click(object sender, RoutedEventArgs e)
        {

            var filtered = _globalFilePaths
                .Where(f =>
                    f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var win = new SearchWindow(filtered);

            win.Owner = this;

            // When an asset is double-clicked inside the SearchWindow
            win.AssetSelected += async (assetPath) =>
            {
                // 1) Switch to Game Packages tab
                MainTabControl.SelectedIndex = 2;

                // 2) Determine folder path of the asset
                string folder = Path.GetDirectoryName(assetPath).Replace("\\", "/");

                // 3) Expand folders
                await ExpandFolderPath(folder);

                // 4) Load assets for that folder
                LoadAssetsForSelectedFolder(folder);

                // 5) Select the asset in the TreeView
                foreach (TreeViewItem item in GamePackagesTreeView.Items)
                {
                    if (item.Tag as string == assetPath)
                    {
                        item.IsSelected = true;

                        // 6) Preview the asset
                        PreviewManager.ShowFilePreviewAsync(
                            item,
                            this,
                            filePath => FindReaderForPath(filePath)
                        );

                        break;
                    }
                }
            };

            // Keep window open
            win.Show();
        }

        private void BtnLoadArchives_Click(object sender, RoutedEventArgs e)
        {
            var profile = App.SelectedProfile;

            if (profile == null)
            {
                MessageBox.Show("Please select a profile first.");
                return;
            }

            // 1) Find Paks directory
            string? paksDir = ResolvePaksDirectory(profile.Directory);
            if (paksDir == null)
            {
                MessageBox.Show("Could not find the game's Paks folder.");
                return;
            }

            // 2) Initialize Oodle
            EnsureOodleInitialized(paksDir);

            // 3) Load AES keys from profile
            var guidKeys = profile.AesGuidKeys
                .ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value);

            var filenameKeys = new Dictionary<string, string>(profile.AesFileKeys);

            // 4) Build pak reader
            _pakReader?.Dispose();
            _pakReader = new AppPakReader(
                paksDir,
                guidKeys,
                filenameKeys,
                profile.MappingPath
            );

            // 5) Build global file list  (FIX #1)
            _globalFilePaths.Clear();
            foreach (var path in _pakReader.EnumerateFilePaths())
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _globalFilePaths.Add(path);
            }

            Console.WriteLine($"[DEBUG] Indexed {_globalFilePaths.Count:N0} files.");

            // 6) Build folder trie  (FIX #2)
            var interner = new StringInterner();
            _folderTrie = new FolderTrie(interner);

            foreach (var raw in _globalFilePaths)
            {
                if (raw.StartsWith("FortniteGame/", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
                {
                    _folderTrie.AddPath(raw);
                }
            }

            _folderTrie.Compact();

            // 7) Update GameFolders UI  (FIX #3)
            BuildFolderTreeUI();

            // 8) Update Archives list (your original line)
            LoadArchivesListUI();

            MessageBox.Show("Archives mounted and loaded.");
        }

        #endregion

        #region Helpers
        private Task FadeOut(UIElement element)
        {
            if (element == null) return Task.CompletedTask;

            var storyboard = (Storyboard)FindResource("PreviewFadeOut");
            Storyboard.SetTarget(storyboard, element);

            var tcs = new TaskCompletionSource<object>();
            storyboard.Completed += (_, __) => tcs.TrySetResult(null);

            storyboard.Begin();
            return tcs.Task;
        }

        private Task FadeIn(UIElement element)
        {
            if (element == null) return Task.CompletedTask;

            var storyboard = (Storyboard)FindResource("PreviewFadeIn");
            Storyboard.SetTarget(storyboard, element);

            storyboard.Begin();
            return Task.CompletedTask;
        }

        private void SelectFileInTree(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            // Switch to Game Packages tab
            MainTabControl.SelectedIndex = 2;

            // Load assets for the folder
            string folder = System.IO.Path.GetDirectoryName(assetPath)
                            ?.Replace("\\", "/") ?? "";

            LoadAssetsForSelectedFolder(folder);

            // Select the file inside the GamePackagesTreeView
            foreach (TreeViewItem item in GamePackagesTreeView.Items)
            {
                if (item.Tag as string == assetPath)
                {
                    item.IsSelected = true;
                    item.BringIntoView();

                    // Preview the asset immediately
                    PreviewManager.ShowFilePreviewAsync(
                        item,
                        this,
                        filePath => FindReaderForPath(filePath)
                    );

                    return;
                }
            }
        }

        private bool SelectInChildren(TreeViewItem node, string assetPath)
        {
            if (node == null)
                return false;

            string nodePath = node.Tag as string ?? "";

            // If this node is EXACTLY the asset → select it
            if (nodePath.Equals(assetPath, StringComparison.OrdinalIgnoreCase))
            {
                node.IsSelected = true;
                node.BringIntoView();
                return true;
            }

            // Only expand if the assetPath is inside this folder
            // (prevents expanding the entire tree!)
            if (!assetPath.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase))
                return false;

            // Now we know this folder is part of the correct path
            node.IsExpanded = true;
            node.UpdateLayout();

            // Recursively search children
            foreach (TreeViewItem child in node.Items)
            {
                if (SelectInChildren(child, assetPath))
                    return true;
            }

            return false;
        }

        public void NavigateToAsset(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            // Remove leading "/"
            string normalized = path.Trim().TrimStart('/');

            // Remove trailing .Something after asset name
            int dot = normalized.LastIndexOf('.');
            if (dot > 0)
                normalized = normalized.Substring(0, dot);

            // Convert /Game/... → FortniteGame/Content/...
            if (normalized.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "FortniteGame/Content/" + normalized.Substring(5);
            }

            // Try matching file
            string uasset = normalized + ".uasset";
            string umap = normalized + ".umap";

            string match = _globalFilePaths.FirstOrDefault(f =>
                f.Equals(uasset, StringComparison.OrdinalIgnoreCase) ||
                f.Equals(umap, StringComparison.OrdinalIgnoreCase)
            );

            if (match != null)
            {
                SelectFileInTree(match);

                // OPTIONAL: auto-switch to packages tab
                MainTabControl.SelectedIndex = 2;

                // OPTIONAL: auto-load the folder’s assets
                string folderPath = System.IO.Path.GetDirectoryName(match).Replace("\\", "/");
                LoadAssetsForSelectedFolder(folderPath);

                return;
            }

            Console.WriteLine($"[WARN] No file matched: {normalized}");
        }



        private string? ResolvePaksDirectory(string dir)
        {
            if (dir.EndsWith("Paks", StringComparison.OrdinalIgnoreCase))
                return dir;

            string[] candidates =
            {
        Path.Combine(dir, "FortniteGame", "Content", "Paks"),
        Path.Combine(dir, "Content", "Paks"),
        Path.Combine(dir, "Paks")
    };

            foreach (var c in candidates)
                if (Directory.Exists(c))
                    return c;

            return null;
        }

        private static bool _oodleInitialized = false;

        private void EnsureOodleInitialized(string paksDir)
        {
            if (_oodleInitialized)
            {
                Console.WriteLine("[Oodle] Already initialized.");
                return;
            }

            try
            {
                // -----------------------
                // 1. Try to load Oodle from the game first
                // -----------------------
                var dir = new DirectoryInfo(paksDir);

                // Paks → Content → FortniteGame
                dir = dir.Parent;
                dir = dir?.Parent;

                string binPath = Path.Combine(dir.FullName, "Binaries", "Win64");

                Console.WriteLine("[Oodle] Searching DLL in: " + binPath);

                string gameDll = Directory
                    .GetFiles(binPath, "oo2core*_win64.dll", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(gameDll) && File.Exists(gameDll))
                {
                    Console.WriteLine("[Oodle] Loading game DLL: " + gameDll);
                    OodleHelper.Initialize(gameDll);
                    _oodleInitialized = true;
                    return;
                }

                Console.WriteLine("[Oodle] WARNING: No Oodle DLL found in game directory.");

                // -----------------------
                // 2. Fallback to project Resources folder
                // -----------------------
                string fallbackPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Resources",
                    "oo2core_5_win64.dll"
                );

                Console.WriteLine("[Oodle] Searching fallback: " + fallbackPath);

                if (!File.Exists(fallbackPath))
                {
                    Console.WriteLine("[Oodle] ERROR: Fallback DLL missing: " + fallbackPath);
                    return;
                }

                Console.WriteLine("[Oodle] Using fallback DLL: " + fallbackPath);
                OodleHelper.Initialize(fallbackPath);

                _oodleInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Oodle] ERROR initializing: " + ex);
            }
        }
        public async void ShowSinglePaneText(string text)
        {
            // fade out old panel
            await FadeOut(SinglePaneGrid);
            await FadeOut(DualPaneGrid);

            SinglePaneGrid.Visibility = Visibility.Visible;
            DualPaneGrid.Visibility = Visibility.Collapsed;

            // Split into lines off UI thread
            _ = Task.Run(() =>
            {
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                Dispatcher.Invoke(() =>
                {
                    JsonList.ItemsSource = lines;
                    JsonList.ScrollIntoView(lines.FirstOrDefault());
                }, System.Windows.Threading.DispatcherPriority.Background);
            });

            // fade in refreshed content
            await FadeIn(SinglePaneGrid);
        }
        private void JsonList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (JsonList.SelectedItem is not string line)
                return;

            int idx = line.IndexOf("/Game/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return;

            // Extract until whitespace, quote, comma, or brace
            int end = idx;
            while (end < line.Length &&
                   !char.IsWhiteSpace(line[end]) &&
                   line[end] != '"' &&
                   line[end] != ',' &&
                   line[end] != '}')
            {
                end++;
            }

            string path = line.Substring(idx, end - idx);

            // This was your missing problem!
            NavigateToAsset(path);
        }

        public async void ShowDualPane(string textLeft, byte[] pngBytes)
        {
            await FadeOut(SinglePaneGrid);
            await FadeOut(DualPaneGrid);

            SinglePaneGrid.Visibility = Visibility.Collapsed;
            DualPaneGrid.Visibility = Visibility.Visible;

            PreviewText_Dual.Text = textLeft;

            // Load image
            BitmapImage bmp = new BitmapImage();
            using var ms = new MemoryStream(pngBytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            PreviewImage.Source = bmp;
            PreviewImage.Visibility = Visibility.Visible;

            await FadeIn(DualPaneGrid);
        }

        private async Task<bool> ExpandFolderPath(string fullPath)
        {
            string[] parts = fullPath.Split('/');

            ItemsControl current = GameFoldersTreeView;
            string accumulated = "";

            foreach (var part in parts)
            {
                accumulated = accumulated == "" ? part : $"{accumulated}/{part}";

                TreeViewItem? found = null;

                foreach (TreeViewItem item in current.Items)
                {
                    if (item.Tag is string tag &&
                        tag.Equals(accumulated, StringComparison.OrdinalIgnoreCase))
                    {
                        found = item;
                        break;
                    }
                }

                if (found == null)
                    return false;

                found.IsExpanded = true;

                // Allow async loading of children
                await Task.Delay(60);

                current = found;
            }

            return true;
        }


        private void ExportReferencedTextures_Click(object sender, RoutedEventArgs e)
        {
            if (GamePackagesTreeView.SelectedItem is not TreeViewItem item)
                return;

            string assetPath = item.Tag as string;
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            PreviewManager.ExportReferencedTextures(assetPath, this, _pakReader);
        }
        private void ExportTextures_Click(object sender, RoutedEventArgs e)
        {
            if (GamePackagesTreeView.SelectedItem is not TreeViewItem item)
                return;

            string assetPath = item.Tag as string;
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            PreviewManager.ExportTexturesFromAsset(assetPath, this, _pakReader);
        }

        private void ExportJson_Click(object sender, RoutedEventArgs e)
        {
            if (GamePackagesTreeView.SelectedItem is not TreeViewItem item)
                return;

            string assetPath = item.Tag as string;
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            PreviewManager.ExportJsonFromAsset(assetPath, this, _pakReader);
        }



        private static string NormalizeFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            path = path.Replace('\\', '/').TrimStart('/');

            path = path.Replace(
                "FortniteGame/Content/FortniteGame/Content",
                "FortniteGame/Content"
            );

            path = path.Replace(
                "Engine/Content/Engine/Content",
                "Engine/Content"
            );

            path = path.Replace("FortniteGame/FortniteGame/", "FortniteGame/");
            path = path.Replace("Engine/Engine/", "Engine/");

            return path.TrimEnd('/');
        }

        private AppPakReader? FindReaderForPath(string path)
        {
            return _pakReader;
        }

        private T FindParent<T>(DependencyObject child)
            where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            while (parentObject != null)
            {
                if (parentObject is T parent)
                    return parent;

                parentObject = VisualTreeHelper.GetParent(parentObject);
            }

            return null;
        }

        #endregion
    }
}
