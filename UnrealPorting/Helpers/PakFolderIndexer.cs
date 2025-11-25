// PakFolderIndexer.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnrealPorting.Helpers
{
    internal static partial class PakFolderIndexer
    {
        public static async Task<FolderTrie> BuildSequentialAsync(
            IReadOnlyDictionary<string, string> aesMap,
            IProgress<(int done, int total)>? progress,
            CancellationToken ct)
        {
            var interner = new StringInterner();
            var trie = new FolderTrie(interner);

            int total = aesMap?.Count ?? 0;
            int done = 0;

            if (aesMap == null || total == 0)
                return trie;

            var seenDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (utocOrPak, aesKey) in aesMap)
            {
                ct.ThrowIfCancellationRequested();

                string pakPath = utocOrPak.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                    ? utocOrPak
                    : Path.ChangeExtension(utocOrPak, ".pak");

                if (!File.Exists(pakPath))
                {
                    Console.WriteLine($"[SKIP] No .pak for {utocOrPak}");
                    progress?.Report((++done, total));
                    continue;
                }

                AppPakReader? reader = null;

                try
                {
                    string pakDir = Path.GetDirectoryName(pakPath)!;

                    // Convert AES → GUID dictionary (PakFolderIndexer originally used filenames)
                    var guidDict = new Dictionary<Guid, string>();

                    // Random GUID fallback (same behavior as before)
                    Guid keyGuid = Guid.NewGuid();
                    guidDict[keyGuid] = aesKey;

                    // No filename-based AES keys here
                    var fileKeyDict = new Dictionary<string, string>();

                    // No mapping file in indexer
                    string mappingFile = "";

                    // FIXED: updated constructor
                    reader = new AppPakReader(
                        pakDir,
                        guidDict,       // GUID keys
                        fileKeyDict,    // filename keys
                        mappingFile     // no map file for indexer
                    );

                    long added = 0;

                    foreach (var raw in reader.EnumerateFilePaths())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(raw))
                            continue;

                        var p = raw.Replace('\\', '/')
                                   .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

                        if (!(p.StartsWith("FortniteGame/", StringComparison.OrdinalIgnoreCase) ||
                              p.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        if (p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                        {
                            AddAllAncestorDirs(p, interner, seenDirs, trie);
                            added++;
                            continue;
                        }

                        AddAllAncestorDirs(p, interner, seenDirs, trie);
                        added++;
                    }

                    Console.WriteLine($"[DEBUG INDEXER] Added/synthesized {added} directory chains from {Path.GetFileName(pakPath)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Indexing {pakPath}: {ex.Message}");
                }
                finally
                {
                    try { reader?.Dispose(); } catch { }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    progress?.Report((++done, total));
                }

                await Task.Yield();
            }

            Console.WriteLine("[DEBUG] Folder trie build complete (sequential, folders-only)");
            return trie;
        }

        private static void AddAllAncestorDirs(
            string normalizedPath,
            StringInterner interner,
            HashSet<string> seenDirs,
            FolderTrie trie)
        {
            int lastSlash = normalizedPath.LastIndexOf('/');
            if (lastSlash < 0) return;

            int start = 0;

            while (true)
            {
                int next = normalizedPath.IndexOf('/', start);
                if (next < 0 || next > lastSlash)
                    break;

                var dir = interner.Intern(normalizedPath.AsSpan(0, next));

                if (IsAllowedRoot(dir) && seenDirs.Add(dir))
                    trie.AddPath(dir);

                start = next + 1;
            }

            var parentDir = interner.Intern(normalizedPath.AsSpan(0, lastSlash));
            if (IsAllowedRoot(parentDir) && seenDirs.Add(parentDir))
                trie.AddPath(parentDir);
        }

        private static bool IsAllowedRoot(string dir)
            => dir.Equals("FortniteGame", StringComparison.Ordinal) ||
               dir.Equals("Engine", StringComparison.Ordinal) ||
               dir.StartsWith("FortniteGame/", StringComparison.Ordinal) ||
               dir.StartsWith("Engine/", StringComparison.Ordinal);
    }
}
