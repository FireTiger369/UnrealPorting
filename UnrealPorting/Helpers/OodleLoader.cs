using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.Compression;

public static class OodleLoader
{
    // Prefer newest like FModel
    private static readonly int[] PreferredVersions = { 9, 8, 7, 6, 5 };

    public static string? CurrentDllPath { get; private set; }

    public static bool Initialize(string gameDir)
    {
        CurrentDllPath = null;

        foreach (var dll in EnumerateCandidates(gameDir))
        {
            if (!File.Exists(dll))
                continue;

            try
            {
                Console.WriteLine($"[OODLE] Trying: {dll}");

                // Just init; no fake self-test
                OodleHelper.Initialize(dll);

                if (OodleHelper.Instance != null)
                {
                    CurrentDllPath = dll;
                    Console.WriteLine($"[OODLE] Using: {dll}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OODLE] Failed init {dll}: {ex.Message}");
            }
        }

        Console.WriteLine("[OODLE] ERROR: No usable oo2core DLL found.");
        return false;
    }

    private static IEnumerable<string> EnumerateCandidates(string gameDir)
    {
        // 1) Project/runtime Resources first
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string resDir = Path.Combine(baseDir, "Resources");

        foreach (int v in PreferredVersions)
            yield return Path.Combine(resDir, $"oo2core_{v}_win64.dll");

        // 2) Then game binaries
        string binDir = Path.Combine(gameDir, "Binaries", "Win64");
        if (Directory.Exists(binDir))
        {
            foreach (int v in PreferredVersions)
                yield return Path.Combine(binDir, $"oo2core_{v}_win64.dll");

            // Any other oo2core*_win64.dll, sorted newest→oldest
            foreach (var other in Directory.GetFiles(binDir, "oo2core*_win64.dll")
                                           .OrderByDescending(ExtractVersion))
            {
                yield return other;
            }
        }
    }

    private static int ExtractVersion(string path)
    {
        // oo2core_9_win64.dll → 9
        string name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('_');

        foreach (var p in parts)
            if (int.TryParse(p, out int n))
                return n;

        return 0;
    }
}
