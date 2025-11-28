using CUE4Parse.Compression;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using UnrealPorting.Helpers;
using UnrealPorting.Properties;
using UnrealPorting.Updater;
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
        private LogsWindow _logsWindow;
        private readonly List<string> _logHistory = new();
        private string? _copiedAssetPath;
        public MainWindow()
        {
            Console.WriteLine("[DEBUG] MainWindow created — UI hooks active");
            ReplaceUpdaterIfNeeded();

            InitializeComponent();
            GamePackagesTreeView.PreviewMouseRightButtonDown += OnPackagesRightClick;
            AddLog("Current version = " + App.CurrentVersion);
            ToastManager.ShowToast(this, "Current version: " + App.CurrentVersion, ToastType.Info);
            AppVersionLabel.Text = $"Version {App.CurrentVersion}";
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
            _ = CheckForUpdatesAsync();
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

                AddLog($"[PROFILE] Saved AES keys for profile '{profile.Name}'");
                ToastManager.ShowToast(this, "AES keys saved to profile.", ToastType.Info);

            }
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
                .Where(f =>
                    f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    (f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
                .Where(f =>
                {
                    // remove prefix
                    string remainder = f.Substring(prefix.Length);

                    // MUST NOT contain a slash -> direct child only
                    return !remainder.Contains('/');
                })
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
            AddLog(profile != null
                ? $"[PROFILE] Switched to: {profile.Name}"
                : "[PROFILE] Cleared");
            ToastManager.ShowToast(this,
                profile != null
                    ? $"Switched to profile: {profile.Name}"
                    : "Cleared profile.",
                ToastType.Info);

            // 1) Clear in-memory AES caches (ONLY OUR LOCAL ONE!)
            _aesKeysDictionary.Clear();

            // 2) Dispose only the AppPakReader instance
            _pakReader?.Dispose();
            _pakReader = null;

            // 3) Clear file lists + folder tree
            _globalFilePaths.Clear();
            _folderTrie = null;

            // Do NOT touch Oodle state – leave it alive
            // _oodleInitialized = false;

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
            win.OnProfileConfirmed += () =>
            {
                BtnLoadArchives.Visibility = Visibility.Visible;
            };
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
                await WithSpinner(async () =>
                {
                    await ExpandFolderPath(folder);
                    LoadAssetsForSelectedFolder(folder);
                });

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
        private void OpenLogsWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_logsWindow == null || !_logsWindow.IsVisible)
            {
                _logsWindow = new LogsWindow();
                _logsWindow.Show();

                // Load full history into the window
                foreach (var msg in _logHistory)
                    _logsWindow.AddLog(msg);
            }
            else
            {
                _logsWindow.Show();
                _logsWindow.Focus();
            }
        }

        private async void BtnLoadArchives_Click(object sender, RoutedEventArgs e)
        {
            var profile = App.SelectedProfile;

            if (profile == null)
            {
                MessageBox.Show("Please select a profile first.");
                return;
            }

            ShowSpinner(); // 🔵 start animation immediately
            BtnLoadArchives.Visibility = Visibility.Collapsed;

            await Task.Run(() =>
            {
                // 1) Find Paks directory
                string? paksDir = ResolvePaksDirectory(profile.Directory);
                if (paksDir == null)
                {
                    Dispatcher.Invoke(() =>
                        MessageBox.Show("Could not find the game's Paks folder."));
                    return;
                }

                // 2) Initialize Oodle
                EnsureOodleInitialized(paksDir);

                // 3) Load AES keys from profile
                var guidKeys = profile.AesGuidKeys
                    .ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value);

                var filenameKeys = new Dictionary<string, string>(profile.AesFileKeys);

                // 4) Build pak reader (HEAVY)
                _pakReader?.Dispose();
                _pakReader = new AppPakReader(
                    paksDir,
                    guidKeys,
                    filenameKeys,
                    profile.MappingPath
                );

                // 5) Build global file list (FIX #1)
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

                // 7 + 8) UI must be updated on dispatcher
                Dispatcher.Invoke(() =>
                {
                    BuildFolderTreeUI();
                    LoadArchivesListUI();
                    ToastManager.ShowToast(this, "Archives mounted and loaded.", ToastType.Success);
                });
            });

            HideSpinner(); // 🔵 hide when background work finishes
        }

        #endregion

        #region Helpers

        private void ReplaceUpdaterIfNeeded()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                string newUpdater = Path.Combine(baseDir, "UnrealPorting.Updater.exe.new");
                string currentUpdater = Path.Combine(baseDir, "UnrealPorting.Updater.exe");
                string oldUpdater = currentUpdater + ".old";

                // If a new updater exists (from last update)
                if (File.Exists(newUpdater))
                {
                    // Delete old updater if exists
                    if (File.Exists(currentUpdater))
                        File.Delete(currentUpdater);

                    // Move new updater into place
                    File.Move(newUpdater, currentUpdater);

                    // Cleanup .old if exists
                    if (File.Exists(oldUpdater))
                        File.Delete(oldUpdater);
                }
            }
            catch
            {
                // Silent — worst case updater stays old version
            }
        }
        public void AddLog(string text)
        {
            Console.WriteLine(text);

            string line = $"[{DateTime.Now:HH:mm:ss}] {text}";

            // Store permanently in buffer
            _logHistory.Add(line);

            // If logs window is open, update it live
            _logsWindow?.AddLog(line);
        }

        public void ShowSpinner()
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            var sb = (Storyboard)FindResource("SpinnerPremium");
            sb.Begin();
        }

        public void HideSpinner()
        {
            var sb = (Storyboard)FindResource("SpinnerPremium");
            sb.Stop();

            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        public async Task WithSpinner(Func<Task> action)
        {
            ShowSpinner();
            try
            {
                await action();
            }
            finally
            {
                HideSpinner();
            }
        }

        private void PillHeader_Archives_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
        }

        private void PillHeader_GameFolders_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
        }

        private void PillHeader_GamePackages_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            MainTabControl.SelectedIndex = 2;
        }
        private int _lastTabIndex = 0;
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

            AddLog($"[WARN] No file matched: {normalized}");
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
                var dir = new DirectoryInfo(paksDir);
                dir = dir.Parent;       // Content
                dir = dir?.Parent;      // FortniteGame
                string gameDir = dir?.FullName ?? "";

                Console.WriteLine("[Oodle] Initializing via fallback loader.");

                bool ok = OodleLoader.Initialize(gameDir);

                if (!ok)
                {
                    Console.WriteLine("[Oodle] ERROR: No working oo2core DLL.");
                    return;
                }

                Console.WriteLine("[Oodle] Initialized with: " + OodleLoader.CurrentDllPath);
                _oodleInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Oodle] ERROR initializing: " + ex);
            }
        }
        // ----------------------
        // UPDATE CHECKER
        // ----------------------
        private async Task CheckForUpdatesAsync()
        {
            const string MANIFEST_URL =
                "https://raw.githubusercontent.com/FireTiger369/UnrealPorting/main/UnrealPorting/Updates/update_manifest.json";

            try
            {
                using var http = new HttpClient();
                string json = await http.GetStringAsync(MANIFEST_URL);

                var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
                if (manifest == null)
                {
                    AddLog("[UPDATE] Manifest was null.");
                    ToastManager.ShowToast(this, "Update check failed (null manifest).", ToastType.Error);
                    return;
                }

                Version current = new Version(App.CurrentVersion);
                Version latest = new Version(manifest.version);

                if (latest > current)
                {
                    AddLog("[UPDATE] Update available!");
                    ToastManager.ShowToast(this, "A new update is available!", ToastType.Warning);

                    Dispatcher.Invoke(() =>
                    {
                        var win = new UpdateWindow(manifest);
                        win.Owner = this;
                        win.ShowDialog();
                    });
                }
                else
                {
                    AddLog("[UPDATE] Already up to date.");
                    ToastManager.ShowToast(this, "You are using the latest version.", ToastType.Info);
                }
            }
            catch (Exception ex)
            {
                AddLog("[UPDATE] Failed to check updates: " + ex.Message);
                ToastManager.ShowToast(this, "Update check failed.", ToastType.Error);
            }
        }
        public async void ShowSinglePaneText(string text)
        {
            // Fade out whichever panel is showing
            if (SinglePaneGrid.Visibility == Visibility.Visible)
                await FadeOut(SinglePaneGrid);
            if (DualPaneGrid.Visibility == Visibility.Visible)
                await FadeOut(DualPaneGrid);

            // Switch to the single-pane view
            SinglePaneGrid.Visibility = Visibility.Visible;
            DualPaneGrid.Visibility = Visibility.Collapsed;

            // Prepare text (off UI thread)
            var lines = await Task.Run(() =>
                text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            );

            // Apply new JSON lines
            JsonList.ItemsSource = lines;
            if (lines.Length > 0)
                JsonList.ScrollIntoView(lines[0]);

            // Fade new content in
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

        private void OnPackagesRightClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;

            while (source != null && source is not TreeViewItem)
                source = VisualTreeHelper.GetParent(source);

            if (source is TreeViewItem tvi && tvi.Tag is string path)
            {
                _copiedAssetPath = path;
                tvi.IsSelected = true;
            }
        }

        private void CopyFullPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_copiedAssetPath))
                Clipboard.SetText(_copiedAssetPath);
            ToastManager.ShowToast(this, "Full asset path copied!", ToastType.Success);
        }
        private void CopyNoExt_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_copiedAssetPath))
                return;

            string noExt = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(_copiedAssetPath)!,
                System.IO.Path.GetFileNameWithoutExtension(_copiedAssetPath)
            ).Replace("\\", "/");

            Clipboard.SetText(noExt);
            ToastManager.ShowToast(this, "Asset path without extension copied!", ToastType.Success);
        }
        private void CopyObjectPath_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_copiedAssetPath))
                return;

            string path = _copiedAssetPath.Replace("\\", "/");

            // remove FortniteGame/Content
            string gameRel = path.Replace("FortniteGame/Content", "", StringComparison.OrdinalIgnoreCase);

            // remove Engine/Content (for Engine assets)
            gameRel = gameRel.Replace("Engine/Content", "/Engine", StringComparison.OrdinalIgnoreCase);

            // prefix
            if (!gameRel.StartsWith("/"))
                gameRel = "/" + gameRel;

            string assetName = System.IO.Path.GetFileNameWithoutExtension(gameRel);

            string objectPath = $"{gameRel}.{assetName}";

            Clipboard.SetText(objectPath);
            ToastManager.ShowToast(this, "Unreal Object Path copied!", ToastType.Success);
        }
        private void CopyBlueprintRef_Click(object sender, RoutedEventArgs e)
        {
            if (GamePackagesTreeView.SelectedItem is not TreeViewItem item || item.Tag is null)
                return;

            string fullPath = item.Tag.ToString()!;

            // Remove extensions (.uasset, .umap, etc.)
            string pathNoExt = System.IO.Path.ChangeExtension(fullPath, null)
                .Replace(".uexp", "")
                .Replace(".ubulk", "");

            //---------------------------------------------------------
            // 1. Normalize path — remove FortniteGame roots
            //---------------------------------------------------------
            string cleaned = pathNoExt;

            cleaned = cleaned.Replace("FortniteGame/Plugins/GameFeatures/", "");
            cleaned = cleaned.Replace("/FortniteGame/Plugins/GameFeatures/", "");

            cleaned = cleaned.Replace("FortniteGame/Content/", "");
            cleaned = cleaned.Replace("/FortniteGame/Content/", "");
            cleaned = cleaned.Replace("FortniteGame/", "");
            cleaned = cleaned.Replace("/FortniteGame/", "");

            // Remove /Content/ that appears INSIDE plugins
            cleaned = cleaned.Replace("/Content/", "/");

            cleaned = cleaned.TrimStart('/');

            //---------------------------------------------------------
            // 2. Fix duplicated folder endings (ex: SS_3200/SS_3200/SS_3200)
            //---------------------------------------------------------
            string[] parts = cleaned.Split('/');
            if (parts.Length >= 3)
            {
                string filename = parts[^1];
                string lastFolder = parts[^2];

                if (lastFolder == filename)
                {
                    // Remove the duplicated last folder
                    cleaned = string.Join("/", parts.Take(parts.Length - 1));
                }
            }

            //---------------------------------------------------------
            // 3. Extract asset name
            //---------------------------------------------------------
            string assetName = System.IO.Path.GetFileName(cleaned);

            //---------------------------------------------------------
            // 4. Build the Unreal object path EXACTLY like UE expects
            //---------------------------------------------------------
            // Example output:
            // /Script/LevelSequence.LevelSequence'/SuperSport_03/Sequencer/SS_3200/SS_3200.SS_3200'
            string ueRef =
                $"/Script/LevelSequence.LevelSequence'/{cleaned}/{assetName}.{assetName}'";

            //---------------------------------------------------------
            // 5. Final JSON block 
            //---------------------------------------------------------
            string json =
        $@"{{
    ""Tagged"": [
        [
            ""Level Sequence"",
            ""{ueRef}""
        ]
    ]
}}";

            Clipboard.SetText(json);

            ToastManager.ShowToast(this, "Blueprint Reference copied!", ToastType.Success);
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
        private void ExportMip_Click(object sender, RoutedEventArgs e)
        {
            if (GamePackagesTreeView.SelectedItem is not TreeViewItem item)
                return;

            string assetPath = item.Tag as string;
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            if (_pakReader == null)
                return;

            if (!_pakReader.Provider.TryGetGameFile(assetPath, out var file))
                return;

            var package = _pakReader.Provider.LoadPackage(file);
            package.DeserializeAllExports();

            var export = package.GetExport(0);

            if (export is UTexture2D tex)
            {
                PreviewManager.ExportSingleTextureMip(tex, this);
            }
            else
            {
                MessageBox.Show("This option is only for single textures (UTexture2D).");
            }
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
