using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace UnrealPorting.Properties;
public static class GameProfileStore
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "UnrealPorting");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "GameProfiles.json");

    public static List<GameProfile> Profiles { get; private set; } = new();

    static GameProfileStore()
    {
        Load();
    }

    public static void Load()
    {
        if (!Directory.Exists(ConfigDir))
            Directory.CreateDirectory(ConfigDir);

        if (!File.Exists(ConfigPath))
        {
            Profiles = new List<GameProfile>();
            Save();
            return;
        }

        var json = File.ReadAllText(ConfigPath);
        Profiles = JsonConvert.DeserializeObject<List<GameProfile>>(json)
                  ?? new List<GameProfile>();
    }

    public static void Save()
    {
        var json = JsonConvert.SerializeObject(Profiles, Formatting.Indented);
        File.WriteAllText(ConfigPath, json);
    }
}
