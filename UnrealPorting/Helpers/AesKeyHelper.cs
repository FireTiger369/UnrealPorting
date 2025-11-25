using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnrealPorting.Helpers
{
    /// <summary>
    /// Filters filename-based AES keys (from your AES window + aes_keys.txt)
    ///   to only: global.utoc and pakchunk1000–1099 *.utoc (no .o.utoc, no *optional*).
    /// Then submits those keys to the provider using the VFS filename match.
    /// </summary>
    public static class AesKeyHelper
    {
        // Allowed: "global.utoc" OR "pakchunk10xx-WindowsClient.utoc" (1000..1099)
        // Disallowed: *.o.utoc, *optional*, anything outside 1000–1099
        private static readonly Regex AllowedUtoc =
            new Regex(@"^(global\.utoc|pakchunk10\d\d-[^-]+\.utoc)$",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsAllowed(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            var name = fileName.Trim();

            // ban overlays / optionals outright
            if (name.EndsWith(".o.utoc", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Contains("optional", StringComparison.OrdinalIgnoreCase)) return false;

            // allow global.utoc
            if (name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase)) return true;

            // allow pakchunk1000–1099 utoc only
            if (AllowedUtoc.IsMatch(name))
            {
                // extra guard on the numeric range (regex already enforces 10xx)
                var chunkDigits = new string(name.SkipWhile(c => !char.IsDigit(c))
                                                 .TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(chunkDigits, out var n))
                    return n >= 1000 && n <= 1099;
            }
            return false;
        }

        /// <summary>
        /// Normalize hex format: strip "0x", remove spaces, uppercase.
        /// Returns null if <64 hex chars>.
        /// </summary>
        private static string? NormalizeHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var h = hex.Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                h = h.Substring(2);
            h = Regex.Replace(h, @"\s+", "");
            return h.Length >= 64 ? h.ToUpperInvariant() : null;
        }

        /// <summary>
        /// Load filename-based keys from aes_keys.txt. Accepts lines like:
        ///   global.utoc=0xABC...
        ///   pakchunk1000-WindowsClient.utoc=ABC...
        /// Ignores GUID=HEX lines and pure-global lines — this loader is *filename-only*.
        /// </summary>
        public static Dictionary<string, FAesKey> LoadFilenameKeys(string aesFilePath)
        {
            var dict = new Dictionary<string, FAesKey>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(aesFilePath)) return dict;

            foreach (var raw in File.ReadAllLines(aesFilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (!line.Contains('=')) continue;

                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;

                var left = parts[0].Trim();          // filename key
                var hex = NormalizeHex(parts[1]);   // normalized hex

                if (string.IsNullOrWhiteSpace(left) || hex is null) continue;

                // only take filename-style entries (we don’t accept 32-char GUIDs here)
                if (left.Length == 32 && Regex.IsMatch(left, "^[0-9a-fA-F]{32}$"))
                    continue;

                dict[left] = new FAesKey(hex);
            }
            return dict;
        }

        /// <summary>
        /// Merge window keys (filename -> hex) with saved keys (filename -> FAesKey),
        /// keep only allowed filenames, return filename -> FAesKey.
        /// </summary>
        public static Dictionary<string, FAesKey> MergeAndFilterFilenameKeys(
            Dictionary<string, string> windowFilenameToHex,
            Dictionary<string, FAesKey> savedFilenameKeys)
        {
            var merged = new Dictionary<string, FAesKey>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string name, string? hex)
            {
                if (!IsAllowed(name)) return;
                var norm = NormalizeHex(hex);
                if (norm is null) return;
                merged[name] = new FAesKey(norm);
            }

            // window (takes priority)
            if (windowFilenameToHex != null)
            {
                foreach (var kv in windowFilenameToHex)
                    TryAdd(kv.Key, kv.Value);
            }

            // saved, only if not overridden
            if (savedFilenameKeys != null)
            {
                foreach (var kv in savedFilenameKeys)
                    if (!merged.ContainsKey(kv.Key) && IsAllowed(kv.Key))
                        merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        /// <summary>
        /// Submit keys by matching VFS filename (we *don’t* guess GUIDs).
        /// This mirrors what already worked for you: filename map → SubmitKey(vfs.EncryptionKeyGuid, key).
        /// </summary>
        public static int SubmitFilenameKeysToProvider(
            DefaultFileProvider provider,
            Dictionary<string, FAesKey> filenameKeys)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (filenameKeys == null || filenameKeys.Count == 0) return 0;

            int submitted = 0;
            var unloaded = provider.UnloadedVfs ?? Array.Empty<IAesVfsReader>();

            foreach (var vfs in unloaded)
            {
                try
                {
                    var name = Path.GetFileName(vfs.Path);
                    if (!IsAllowed(name)) continue;

                    if (filenameKeys.TryGetValue(name, out var key))
                    {
                        provider.SubmitKey(vfs.EncryptionKeyGuid, key);
                        submitted++;
                        Console.WriteLine($"[AES] Submitted (filename) {name} -> {vfs.EncryptionKeyGuid:N}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Submit failed for {vfs.Path}: {ex.Message}");
                }
            }
            return submitted;
        }
    }
}
