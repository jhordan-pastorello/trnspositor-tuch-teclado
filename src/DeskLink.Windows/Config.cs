using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink;

public sealed class Config
{
    public static Config Current { get; private set; } = new();

    public bool Enabled { get; set; } = true;
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 47810;
    public int DiscoveryPort { get; set; } = 47811;
    public string ServerFingerprint { get; set; } = "";
    public int SwitchDelayMs { get; set; } = 250;
    public double Speed { get; set; } = 1.0;
    public bool ToggleHotkeyEnabled { get; set; } = true;
    /// <summary>Borda que leva ao Linux: top, bottom, left ou right.</summary>
    public string Edge { get; set; } = "top";

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskLink");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (loaded != null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write("config load falhou: " + ex.Message);
        }
        Current.Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("config save falhou: " + ex.Message);
        }
    }
}
