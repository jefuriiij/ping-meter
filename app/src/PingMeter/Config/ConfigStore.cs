using System.Text.Json;
using System.Text.Json.Serialization;

namespace PingMeter.Config;

public static class ConfigStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingMeter");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                if (config != null)
                {
                    config.Normalize();
                    return config;
                }
            }
        }
        catch
        {
            // corrupt/unreadable settings -> fall through to defaults
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
        }
        catch
        {
            // saving is best-effort; never take the app down over it
        }
    }
}
