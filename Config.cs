// Config.cs (62 lines)
using System.Text.Json;
using evren_cli.Models;

namespace evren_cli;

public sealed class CliConfig
{
    public const string DefaultBaseUrl = "https://evren-llmapi.ssyz.org.tr/v1";
    public const string DefaultModel = "auto";

    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".evren-cli");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static CliConfig Load(out bool created)
    {
        created = false;
        CliConfig? config = null;

        if (File.Exists(ConfigPath))
        {
            try
            {
                config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), EvrenJsonContext.Default.CliConfig);
            }
            catch (JsonException)
            {
                config = null;
            }
        }

        if (config is null)
        {
            config = new CliConfig { ApiKey = "", Model = DefaultModel, BaseUrl = DefaultBaseUrl };
            config.Save();
            created = true;
        }

        if (string.IsNullOrWhiteSpace(config.Model))
            config.Model = DefaultModel;
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            config.BaseUrl = DefaultBaseUrl;

        var envKey = Environment.GetEnvironmentVariable("EVREN_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
            config.ApiKey = envKey;

        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, EvrenJsonContext.Default.CliConfig));
    }
}
