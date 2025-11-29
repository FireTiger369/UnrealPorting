using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem; // IAesVfsReader
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealPorting.Helpers
{
    public class AppPakReader : IDisposable
    {
        private readonly DefaultFileProvider _provider;
        public DefaultFileProvider Provider => _provider;
        private readonly HashSet<string> _uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _mappingFile;
        public string MappingPath => _mappingFile;
        private LogsWindow _logsWindow;
        private readonly List<string> _logHistory = new();

        // ===============================================
        // PATH B1 HOTFIX:
        // Fortnite 35.x UTOC TOC version is newer than CUE4Parse 1.2.2 supports.
        // We detect TOC version > 8 and skip mounting those UTOCs.
        // ===============================================

        // CUE4Parse FIoStoreTocHeader.TOC_MAGIC, copied from your decompile
        private static readonly byte[] TOC_MAGIC = new byte[16]
        {
            45, 61, 61, 45, 45, 61, 61, 45, 45, 61,
            61, 45, 45, 61, 61, 45
        };

        private static bool TryGetUtocTocVersion(string utocPath, out int version)
        {
            version = -1;
            try
            {
                using var fs = File.OpenRead(utocPath);
                using var br = new BinaryReader(fs);

                var magic = br.ReadBytes(16);
                if (magic.Length != 16 || !magic.SequenceEqual(TOC_MAGIC))
                    return false;

                // In UE IoStore, TocVersion is stored as a single byte.
                // CUE4Parse reads EIoStoreTocVersion as its underlying type (byte).
                version = br.ReadByte();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public AppPakReader(
            string pakDirectoryOrFile,
            Dictionary<Guid, string> guidKeys,
            Dictionary<string, string> filenameKeys,
            string mappingFile)
        {
            if (string.IsNullOrWhiteSpace(pakDirectoryOrFile))
                throw new ArgumentNullException(nameof(pakDirectoryOrFile));

            _mappingFile = mappingFile;

            string pakDirectory = File.Exists(pakDirectoryOrFile)
                ? Path.GetDirectoryName(pakDirectoryOrFile)!
                : pakDirectoryOrFile;

            if (!Directory.Exists(pakDirectory))
                throw new DirectoryNotFoundException($"PAK path not found: {pakDirectoryOrFile}");

            // ✔ Use engine version from ACTIVE PROFILE
            var profile = App.SelectedProfile;

            EGame version = profile != null
                ? profile.GetEGameValue()
                : EGame.GAME_UE5_4;

            _provider = new DefaultFileProvider(
                pakDirectory,
                SearchOption.TopDirectoryOnly,
                isCaseInsensitive: true,
                new VersionContainer(version)
            );
            AddLog($"Using engine version: {version}");

            int totalKeys = (guidKeys?.Count ?? 0) + (filenameKeys?.Count ?? 0);
            Console.WriteLine($"[LOAD] Mounting archives in {pakDirectory} (keys: {totalKeys})");

            // 1) Filter needed utoc/ucas/pak files (exclude signatures, optional, o.*)
            string[] allFiles = Directory.GetFiles(pakDirectory, "*.*", SearchOption.TopDirectoryOnly);
            List<string> files = new();

            foreach (string file in allFiles)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();

                if (name.EndsWith(".sig")) continue;
                if (name.Contains("optional")) continue;
                if (name.Contains(".o.utoc") || name.Contains(".o.ucas")) continue;

                if (name.EndsWith(".utoc") || name.EndsWith(".ucas") || name.EndsWith(".pak"))
                {
                    files.Add(file);
                }
            }

            Console.WriteLine($"[INFO] Filter selected {files.Count} archives for mount.");

            // Register with provider
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                try
                {
                    _provider.RegisterVfs(file);
                    Console.WriteLine($"[REGISTER] {name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to register {name}: {ex.Message}");
                    AddLog($"Failed to register {name}: {ex.Message}");
                }
            }

            var stray = _provider.UnloadedVfs?
                .Where(v =>
                    v.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                    v.Name.EndsWith(".o.utoc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stray is { Count: > 0 })
            {
                Console.WriteLine($"[CLEANUP] Detected {stray.Count} stray .pak/.o.utoc archives (will skip mounting)...");
            }

            // 2) AES keys
            var savedGuidMap = new Dictionary<FGuid, FAesKey>();
            var filenameKeyMap = new Dictionary<string, FAesKey>(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"[AES] Loaded {savedGuidMap.Count} GUID key(s) and {filenameKeyMap.Count} filename-based key(s) from aes_keys.txt");

            foreach (var kv in guidKeys ?? new())
            {
                var hex = kv.Value.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                savedGuidMap[new FGuid(kv.Key.ToString("N"))] = new FAesKey(hex);
            }

            foreach (var kv in filenameKeys ?? new())
            {
                var hex = kv.Value.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                filenameKeyMap[kv.Key] = new FAesKey(hex);
            }

            string mappingPath = _mappingFile;

            // Expand filename map
            var expandedFilenameMap = new Dictionary<string, FAesKey>(filenameKeyMap, StringComparer.OrdinalIgnoreCase);

            // Load AES keys from active profile
            if (profile != null)
            {
                Console.WriteLine($"[PROFILE] Loading AES keys for profile '{profile.Name}'");

                foreach (var kv in profile.AesFileKeys)
                {
                    string fileName = kv.Key.Trim();
                    string hex = kv.Value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

                    var aes = new FAesKey(hex);
                    filenameKeyMap[fileName] = aes;
                    expandedFilenameMap[fileName] = aes;

                    Console.WriteLine($"    [FILE] {fileName} → {hex}");
                }

                foreach (var kv in profile.AesGuidKeys)
                {
                    if (Guid.TryParse(kv.Key, out Guid guid))
                    {
                        string hex = kv.Value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                        savedGuidMap[new FGuid(guid.ToString("N"))] = new FAesKey(hex);
                        Console.WriteLine($"    [GUID] {guid} → {hex}");
                    }
                }

                Console.WriteLine($"[PROFILE] AES keys successfully merged into runtime maps.");
            }
            else
            {
                Console.WriteLine("[PROFILE] No active profile → cannot load profile AES keys.");
                AddLog("[PROFILE] No active profile → cannot load profile AES keys.");
            }

            // Expand UTOC ↔ UCAS, and ALSO (hotfix) UTOC → PAK if pak exists.
            foreach (var kv in filenameKeyMap)
            {
                var name = kv.Key;
                var key = kv.Value;

                if (name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = name.Substring(0, name.Length - 5);

                    expandedFilenameMap[name] = key;
                    expandedFilenameMap[baseName + ".ucas"] = key;

                    // HOTFIX: also map to legacy pak if it exists
                    string pakCandidate = baseName + ".pak";
                    if (File.Exists(Path.Combine(pakDirectory, pakCandidate)))
                    {
                        expandedFilenameMap[pakCandidate] = key;
                    }

                    continue;
                }

                if (name.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = name.Substring(0, name.Length - 5);
                    expandedFilenameMap[name] = key;
                    expandedFilenameMap[baseName + ".utoc"] = key;
                    continue;
                }

                expandedFilenameMap[name] = key;
            }

            // 3) Submit AES keys
            int submitted = 0;
            var unloaded = _provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>();

            foreach (var vfs in unloaded)
            {
                try
                {
                    string filename = Path.GetFileName(vfs.Path);
                    bool matchedKey = false;

                    if (expandedFilenameMap.TryGetValue(filename, out var fnKey))
                    {
                        _provider.SubmitKey(vfs.EncryptionKeyGuid, fnKey);
                        Console.WriteLine($"[AES] Submitted AES for {filename} (exact match)");
                        submitted++;
                        matchedKey = true;
                    }
                    else if (vfs.EncryptionKeyGuid.IsValid() &&
                             savedGuidMap.TryGetValue(vfs.EncryptionKeyGuid, out var guidKey))
                    {
                        _provider.SubmitKey(vfs.EncryptionKeyGuid, guidKey);
                        Console.WriteLine($"[AES] Submitted AES by GUID for {filename}");
                        submitted++;
                        matchedKey = true;
                    }

                    // Global fallback for global.ucas
                    if (!matchedKey)
                    {
                        string lower = filename.ToLowerInvariant();
                        if (lower == "global.ucas" &&
                            filenameKeyMap.TryGetValue("global.utoc", out var globalKey))
                        {
                            _provider.SubmitKey(vfs.EncryptionKeyGuid, globalKey);
                            Console.WriteLine($"[AES] GLOBAL FALLBACK → {filename}");
                            submitted++;
                            matchedKey = true;
                        }
                    }

                    if (!matchedKey)
                    {
                        Console.WriteLine($"[WARN] No AES found for {filename}");
                        AddLog  ($"No AES key found for {filename}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to submit AES for {vfs.Name}: {ex.Message}");
                    AddLog  ($"Failed to submit AES for {vfs.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"[AES] Submitted {submitted} AES keys to provider.");

            // Force global.utoc key if missing
            try
            {
                if (filenameKeyMap.TryGetValue("global.utoc", out var globalKey))
                {
                    foreach (var vfs in _provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>())
                    {
                        string lower = Path.GetFileName(vfs.Path).ToLowerInvariant();
                        if (lower == "global.utoc")
                        {
                            if (!vfs.EncryptionKeyGuid.IsValid() ||
                                !_provider.Keys.ContainsKey(vfs.EncryptionKeyGuid))
                            {
                                _provider.SubmitKey(vfs.EncryptionKeyGuid, globalKey);
                                Console.WriteLine($"[AES] Forced global AES ␦ {lower}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AES] ERROR applying global key fallback: {ex.Message}");
                AddLog  ($"[AES] ERROR applying global key fallback: {ex.Message}");
            }

            // 4) Controlled mounting w/ B1 utoc-skip hotfix
            int mounted = 0;
            var toMount = (_provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>())
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"[MOUNT] Preparing to mount {toMount.Count} archives with stabilization...");

            foreach (var vfs in toMount)
            {
                string name = vfs.Name;
                string path = vfs.Path;

                // PATH B1: pre-check UTOC TOC version, skip if unsupported
                if (name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryGetUtocTocVersion(path, out int tocVer))
                        {
                            Console.WriteLine($"[MOUNT] → {name} ... SKIP (unknown/new UTOC format)");
                            continue;
                        }

                        if (tocVer > 8) // anything newer than UE5.4 era
                        {
                            Console.WriteLine($"[MOUNT] → {name} ... SKIP (unsupported TOC v{tocVer})");
                            continue;
                        }
                    }
                }

                try
                {
                    Console.Write($"[MOUNT] → {name} ... ");
                    vfs.Mount(StringComparer.OrdinalIgnoreCase);
                    mounted++;
                    Console.WriteLine(vfs.HasDirectoryIndex ? "OK" : "OK (no index)");
                    System.Threading.Thread.Sleep(35);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL: {ex.Message}");
                    AddLog($"Failed to mount {name}: {ex.Message}");
                }
            }

            Console.WriteLine($"[MOUNT] Mounted {mounted}/{toMount.Count} archives successfully.");

            // 5) Diagnostic for empty encrypted archives
            var emptyArchives = _provider.MountedVfs
                .Where(v => v.IsEncrypted && (v.Files?.Count ?? 0) == 0)
                .ToList();

            if (emptyArchives.Count > 0)
            {
                Console.WriteLine($"[WARN] {emptyArchives.Count} mounted archives have 0 decrypted files:");
                foreach (var v in emptyArchives.Take(10))
                    Console.WriteLine($"   {v.Name}");
            }

            // 6) Initialize provider
            try
            {
                _provider.Initialize();
                Console.WriteLine($"[OK] Provider initialized with {_provider.Files.Count:N0} files total.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Provider initialize failed: {ex.Message}");
                AddLog  ($"Provider initialize failed: {ex.Message}");
            }

            // 7) Second-pass stabilization
            int before = _provider.Files.Count;
            System.Threading.Thread.Sleep(300);
            _provider.Initialize();
            int after = _provider.Files.Count;

            if (after > before)
                Console.WriteLine($"[STABILIZE] Added {after - before:N0} late-resolving files (total now {after:N0}).");
            else
                Console.WriteLine("[STABILIZE] No late additions after re-scan.");

            // 8) Load mappings after mount + init
            if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
            {
                Console.WriteLine("[MAPPINGS] Applying mappings after mount: " + mappingPath);
                _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);

                try
                {
                    _provider.Initialize();
                    Console.WriteLine("[MAPPINGS] Provider reinitialized with mappings.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Mapping load failed: {ex.Message}");
                    AddLog  ($"Mapping load failed: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[MAPPINGS] No profile mapping found, skipping.");
                AddLog  ("No profile mapping found, skipping.");
            }

            PrintProviderSummary();
        }

        private void PrintProviderSummary()
        {
            Console.WriteLine($"[DEBUG] Mounted VFS count: {_provider.MountedVfs?.Count ?? 0}");
            foreach (var vfs in _provider.MountedVfs.Take(10))
                Console.WriteLine($"   {vfs.Name} | Encrypted: {vfs.IsEncrypted} | Files: {vfs.Files?.Count ?? 0}");
        }

        public IEnumerable<string> EnumerateFilePaths()
        {
            if (_provider.Files.Count == 0)
            {
                Console.WriteLine("[WARN] No files found in provider.");
                yield break;
            }

            foreach (var kv in _provider.Files)
            {
                string path = kv.Key.Replace("\\", "/");
                if (_uniquePaths.Add(path))
                    yield return path;
            }

            Console.WriteLine($"[OK] Enumerated {_uniquePaths.Count:N0} unique files.");
        }

        public byte[]? ReadFileBytes(string path)
        {
            if (_provider.Files.TryGetValue(path, out var file))
                return file.Read();
            return null;
        }

        public string? ReadFileAsText(string path)
        {
            var data = ReadFileBytes(path);
            return data == null ? null : System.Text.Encoding.UTF8.GetString(data);
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

        public void Dispose() => _provider.Dispose();
    }
}
