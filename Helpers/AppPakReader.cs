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

            // ✔ Use engine version from the ACTIVE PROFILE (correct behavior)
            var profile = App.SelectedProfile;

            EGame version = profile != null
                ? profile.GetEGameValue()  // we will implement this below
                : EGame.GAME_UE5_4;

            // ✔ Use version from profile, NOT from settings
            _provider = new DefaultFileProvider(
                pakDirectory,
                SearchOption.TopDirectoryOnly,
                isCaseInsensitive: true,
                new VersionContainer(version)
            );

            int totalKeys = (guidKeys?.Count ?? 0) + (filenameKeys?.Count ?? 0);
            Console.WriteLine($"[LOAD] Mounting archives in {pakDirectory} (keys: {totalKeys})");

            // 1) Filter needed utoc/pak files (global.utoc + pakchunk10*, exclude *.o.utoc & *optional*)
            string[] allFiles = Directory.GetFiles(pakDirectory, "*.*", SearchOption.TopDirectoryOnly);
            List<string> files = new List<string>();

            foreach (string file in allFiles)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();

                // Skip signatures + o.* chunks
                if (name.EndsWith(".sig")) continue;
                if (name.Contains("optional")) continue;
                if (name.Contains(".o.utoc") || name.Contains(".o.ucas")) continue;

                // IOStore containers
                if (name.EndsWith(".utoc") || name.EndsWith(".ucas"))
                {
                    files.Add(file);
                    continue;
                }

                // (Optional) legacy .pak support
                // if (name.EndsWith(".pak"))
                // {
                //     files.Add(file);
                //     continue;
                // }
            }

            Console.WriteLine($"[INFO] Filter selected {files.Count} archives for mount.");

            // ✅ Actually register them with the provider
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
                }
            }

            // Double-check for stray .pak files in provider
            var stray = _provider.UnloadedVfs?
                .Where(v =>
                    v.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                    v.Name.EndsWith(".o.utoc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (stray is { Count: > 0 })
            {
                Console.WriteLine($"[CLEANUP] Detected {stray.Count} stray .pak/.o.utoc archives (will skip mounting)...");
            }

            // 2) Load keys from aes_keys.txt (GUID-based + filename-based)

            var savedGuidMap = new Dictionary<FGuid, FAesKey>();
            var filenameKeyMap = new Dictionary<string, FAesKey>(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"[AES] Loaded {savedGuidMap.Count} GUID key(s) and {filenameKeyMap.Count} filename-based key(s) from aes_keys.txt");

            // Merge GUID-based keys from runtime
            foreach (var kv in guidKeys)
            {
                var hex = kv.Value.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                savedGuidMap[new FGuid(kv.Key.ToString("N"))] = new FAesKey(hex);
            }

            // Merge filename-based keys from runtime
            foreach (var kv in filenameKeys)
            {
                var hex = kv.Value.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                filenameKeyMap[kv.Key] = new FAesKey(hex);
            }

            // Load .usmap mappings BEFORE Initialize()
            // ✔ Correct mapping source: profile.MappingPath
            string mappingPath = _mappingFile; // this was passed into constructor

            // 1. Try profile mapping file
            if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
            {
                Console.WriteLine("[MAPPINGS] Using profile mapping: " + mappingPath);
                _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
            }
            else
            {
                Console.WriteLine("[MAPPINGS] No mapping file set for profile.");

                // 2. Fallback: try auto-detect mappings folder in project
                string autoMappings = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Mappings",
                    "default.usmap"
                );

                if (File.Exists(autoMappings))
                {
                    Console.WriteLine("[MAPPINGS] Using fallback: " + autoMappings);
                    _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(autoMappings);
                }
                else
                {
                    Console.WriteLine("[MAPPINGS] No fallback mapping found. Continuing without mappings.");
                }
            }

            // 2b) Expand filename map to auto-map .utoc, .ucas, .pak, .uexp
            var expandedFilenameMap = new Dictionary<string, FAesKey>(filenameKeyMap, StringComparer.OrdinalIgnoreCase);

            // ===============================================
            // LOAD AES KEYS FROM ACTIVE PROFILE (CONVERT TO FAesKey)
            // ===============================================

            if (profile != null)
            {
                Console.WriteLine($"[PROFILE] Loading AES keys for profile '{profile.Name}'");

                // Load filename-based AES (pakchunk1000-WindowsClient.utoc)
                foreach (var kv in profile.AesFileKeys)
                {
                    string fileName = kv.Key.Trim();
                    string hex = kv.Value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

                    var aes = new FAesKey(hex);

                    filenameKeyMap[fileName] = aes;
                    expandedFilenameMap[fileName] = aes;

                    Console.WriteLine($"    [FILE] {fileName} → {hex}");
                }

                // Load GUID-based AES keys
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
            }


            foreach (var kv in filenameKeyMap)
            {
                var name = kv.Key;
                var key = kv.Value;
                string baseName;

                // UTOC → UCAS + UEXP + PAK (legacy)
                if (name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = name.Substring(0, name.Length - 5);

                    expandedFilenameMap[name] = key; // .utoc itself
                    expandedFilenameMap[baseName + ".ucas"] = key;
                    expandedFilenameMap[baseName + ".uexp"] = key;
                    expandedFilenameMap[baseName + ".pak"] = key;

                    continue;
                }

                // UCAS → UTOC + UEXP
                if (name.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = name.Substring(0, name.Length - 5);

                    expandedFilenameMap[name] = key; // .ucas itself
                    expandedFilenameMap[baseName + ".utoc"] = key;
                    expandedFilenameMap[baseName + ".uexp"] = key;

                    continue;
                }

                // PAK → UTOC + UCAS + UEXP
                if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = name.Substring(0, name.Length - 4);

                    expandedFilenameMap[name] = key; // .pak itself
                    expandedFilenameMap[baseName + ".utoc"] = key;
                    expandedFilenameMap[baseName + ".ucas"] = key;
                    expandedFilenameMap[baseName + ".uexp"] = key;

                    continue;
                }

                // UEXP (rare during IOStore, but safe)
                if (name.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = name.Substring(0, name.Length - 5);

                    expandedFilenameMap[name] = key;
                    expandedFilenameMap[baseName + ".utoc"] = key;
                    expandedFilenameMap[baseName + ".ucas"] = key;

                    continue;
                }

                // Default fallback
                expandedFilenameMap[name] = key;
            }


            // Normalizer (unused currently, but safe to keep for future)
            string NormalizePakName(string name)
            {
                return name
                    .Replace("_Event", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_Evergreen", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_Release", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_Optional", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_PAX", "", StringComparison.OrdinalIgnoreCase);
            }

            // 3) Submit AES keys (filename first, then GUID)
            int submitted = 0;
            var unloaded = _provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>();

            foreach (var vfs in unloaded)
            {
                try
                {
                    string filename = Path.GetFileName(vfs.Path);

                    if (expandedFilenameMap.TryGetValue(filename, out var fnKey))
                    {
                        _provider.SubmitKey(vfs.EncryptionKeyGuid, fnKey);
                        Console.WriteLine($"[AES] Submitted AES for {filename} (exact match)");
                        submitted++;
                        continue;
                    }

                    if (vfs.EncryptionKeyGuid.IsValid() &&
                        savedGuidMap.TryGetValue(vfs.EncryptionKeyGuid, out var guidKey))
                    {
                        _provider.SubmitKey(vfs.EncryptionKeyGuid, guidKey);
                        Console.WriteLine($"[AES] Submitted AES by GUID for {filename}");
                        submitted++;
                    }
                    else
                    {
                        Console.WriteLine($"[WARN] No AES found for {filename}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to submit AES for {vfs.Name}: {ex.Message}");
                }
            }
            Console.WriteLine($"[AES] Submitted {submitted} AES keys to provider.");

            // 🔵 AUTO-ASSIGN GLOBAL AES TO SELECTED CHUNKS (optional)
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
                else
                {
                    Console.WriteLine("[AES] ERROR: global.utoc key missing.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AES] ERROR applying global key fallback: {ex.Message}");
            }

            // 4) Controlled mounting
            int mounted = 0;
            var toMount = (_provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>())
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"[MOUNT] Preparing to mount {toMount.Count} archives with stabilization...");

            foreach (var vfs in toMount)
            {
                try
                {
                    Console.Write($"[MOUNT] → {vfs.Name} ... ");
                    vfs.Mount(StringComparer.OrdinalIgnoreCase);
                    mounted++;
                    Console.WriteLine(vfs.HasDirectoryIndex ? "OK" : "OK (no index)");

                    System.Threading.Thread.Sleep(35);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL: {ex.Message}");
                }
            }

            Console.WriteLine($"[MOUNT] Mounted {mounted}/{toMount.Count} archives successfully.");

            // 5) Diagnostic check for empty encrypted archives
            var emptyArchives = _provider.MountedVfs
                .Where(v => v.IsEncrypted && (v.Files?.Count ?? 0) == 0)
                .ToList();

            if (emptyArchives.Count > 0)
            {
                Console.WriteLine($"[WARN] {emptyArchives.Count} mounted archives have 0 decrypted files:");
                foreach (var v in emptyArchives.Take(10))
                    Console.WriteLine($"   {v.Name}");
            }

            // 6) Initialize provider (first pass)
            try
            {
                _provider.Initialize();
                Console.WriteLine($"[OK] Provider initialized with {_provider.Files.Count:N0} files total.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Provider initialize failed: {ex.Message}");
            }

            // 7) Second-pass enumeration to stabilize missed entries
            int before = _provider.Files.Count;
            System.Threading.Thread.Sleep(300);
            _provider.Initialize();
            int after = _provider.Files.Count;

            if (after > before)
                Console.WriteLine($"[STABILIZE] Added {after - before:N0} late-resolving files (total now {after:N0}).");
            else
                Console.WriteLine("[STABILIZE] No late additions after re-scan.");

            // Optional: show still-encrypted archives we couldn't decrypt
            var stillEncrypted = (_provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>())
                .Where(v => v.IsEncrypted)
                .Select(v => Path.GetFileName(v.Path))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (stillEncrypted.Count > 0)
            {
                Console.WriteLine($"[WARN] {stillEncrypted.Count} encrypted archives remain without keys (first 10):");
                foreach (var n in stillEncrypted.Take(10))
                    Console.WriteLine($"   {n}");
            }

            PrintProviderSummary();
        }

        // GUID map
        private static Dictionary<FGuid, FAesKey> LoadAesKeys(string path)
        {
            var guidKeys = new Dictionary<FGuid, FAesKey>();
            if (!File.Exists(path)) return guidKeys;

            foreach (string raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                if (!line.Contains('=')) continue;

                var parts = line.Split('=', 2);
                string left = parts[0].Trim();
                string hex = parts[1].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

                if (left.Length == 32 && left.All(Uri.IsHexDigit))
                {
                    guidKeys[new FGuid(left.ToLower())] = new FAesKey(hex);
                }
            }

            Console.WriteLine($"[AES] Loaded {guidKeys.Count} GUID keys.");
            return guidKeys;
        }

        // Filename map
        private static Dictionary<string, FAesKey> LoadFilenameKeys(string path)
        {
            var fileKeys = new Dictionary<string, FAesKey>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return fileKeys;

            foreach (string raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                if (!line.Contains('=')) continue;

                var parts = line.Split('=', 2);
                string left = parts[0].Trim();
                string hex = parts[1].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

                if (!(left.Length == 32 && left.All(Uri.IsHexDigit)))
                {
                    fileKeys[left] = new FAesKey(hex);
                }
            }

            Console.WriteLine($"[AES] Loaded {fileKeys.Count} filename-based keys.");
            return fileKeys;
        }

        private static string FindOodleDll(string pakDirectory)
        {
            DirectoryInfo? d = new DirectoryInfo(pakDirectory);
            while (d != null && !d.Name.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
                d = d.Parent;

            if (d == null)
                return "";

            string win64 = Path.Combine(d.FullName, "Binaries", "Win64");
            string dll = Directory.GetFiles(win64, "oo2core*_win64.dll", SearchOption.TopDirectoryOnly)
                                  .FirstOrDefault();

            if (!string.IsNullOrEmpty(dll))
                Console.WriteLine($"[Oodle] Found game Oodle DLL: {dll}");
            else
                Console.WriteLine("[Oodle] No Oodle DLL found in game directory!");

            return dll ?? "";
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

        public void Dispose() => _provider.Dispose();
    }
}
