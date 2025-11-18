using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

public static class OodleLoader
{
    public static void InitializeFromGameDir(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir))
        {
            Console.WriteLine("[ERROR] Game directory is null or empty.");
            return;
        }

        string baseDir = gameDir;
        DirectoryInfo dir = new DirectoryInfo(baseDir);

        // Handle the exact case: FortniteGame/Content/Paks
        if (dir.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
            dir.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true &&
            dir.Parent?.Parent?.Name.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) == true)
        {
            baseDir = dir.Parent.Parent.FullName;
        }
        else
        {
            // Go up until FortniteGame is found
            while (dir != null && !dir.Name.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
                dir = dir.Parent;

            if (dir != null)
                baseDir = dir.FullName;
        }

        string binariesPath = Path.Combine(baseDir, "Binaries", "Win64");

        if (!Directory.Exists(binariesPath))
        {
            Console.WriteLine($"[ERROR] Could not find Binaries folder at {binariesPath}");
            return;
        }

        // Find any oo2core DLL
        string dll = Directory.GetFiles(binariesPath, "oo2core*_win64.dll", SearchOption.TopDirectoryOnly)
                              .FirstOrDefault();

        if (string.IsNullOrEmpty(dll))
        {
            Console.WriteLine($"[WARN] No oo2core DLL found in {binariesPath}");
            return;
        }

        try
        {
            // ONLY load the DLL into the process.
            NativeLibrary.Load(dll);
            Console.WriteLine($"[INFO] Oodle DLL loaded into process: {dll}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load Oodle DLL: {ex.Message}");
        }
    }
}
