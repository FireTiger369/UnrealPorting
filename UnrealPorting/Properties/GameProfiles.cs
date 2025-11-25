using CUE4Parse.UE4.Versions;

public class GameProfile
{
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string EngineVersion { get; set; } = "Unreal Engine 5.4";

    // NEW FIXED PROPERTY
    public int EngineVersionIndex { get; set; } = 14;

    public Dictionary<string, string> AesFileKeys { get; set; } = new();
    public Dictionary<string, string> AesGuidKeys { get; set; } = new();
    public string MappingPath { get; set; } = "";

    public EGame GetEGameValue()
    {
        return EngineVersionIndex switch
        {
            0 => EGame.GAME_UE4_16,
            1 => EGame.GAME_UE4_19,
            2 => EGame.GAME_UE4_20,
            3 => EGame.GAME_UE4_21,
            4 => EGame.GAME_UE4_22,
            5 => EGame.GAME_UE4_23,
            6 => EGame.GAME_UE4_24,
            7 => EGame.GAME_UE4_25,
            8 => EGame.GAME_UE4_26,
            9 => EGame.GAME_UE4_27,
            10 => EGame.GAME_UE5_0,
            11 => EGame.GAME_UE5_1,
            12 => EGame.GAME_UE5_2,
            13 => EGame.GAME_UE5_3,
            14 => EGame.GAME_UE5_4,
            15 => EGame.GAME_UE5_5,
            16 => EGame.GAME_UE5_6,
            17 => EGame.GAME_UE5_7,
            18 => EGame.GAME_UE5_8,
            _ => EGame.GAME_UE5_4
        };
    }
}
