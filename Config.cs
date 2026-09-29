// Config.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using evren_cli.Models;

namespace evren_cli;

public sealed class CliConfig
{
    public const string DefaultBaseUrl = "https://evren-llmapi.ssyz.org.tr/v1";
    public const string DefaultModel = "auto";

    /// <summary>Geriye dönük alan: tek anahtar. Okunur; kaydederken <see cref="ApiKeys"/> ile birleştirilir.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Birden çok API anahtarı. Biri bittiğinde sıradakine otomatik geçilir.</summary>
    public List<string> ApiKeys { get; set; } = new();

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

        // Eski tek-anahtar alanını yeni listeye taşı (geriye dönük uyum).
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            config.AddKey(config.ApiKey);
            config.ApiKey = null;
        }

        // Env değişkeniyle birden çok anahtar desteklenir: virgül / noktalı virgül / boşlukla ayrılır.
        var envKeys = Environment.GetEnvironmentVariable("EVREN_API_KEY");
        foreach (var key in SplitKeys(envKeys))
            config.AddKey(key, atFront: true);

        config.ApiKeys = config.ApiKeys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

        return config;
    }

    /// <summary>Tek bir "evren_llm_..." metnini birden çok anahtara ayırır.</summary>
    public static IEnumerable<string> SplitKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var part in raw.Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var key = part.Trim();
            if (key.Length > 0)
                yield return key;
        }
    }

    /// <summary>Anahtar ekler (mükerrerleri atlar). <paramref name="atFront"/> env anahtarları için öne alır.</summary>
    public bool AddKey(string key, bool atFront = false)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0 || ApiKeys.Contains(key))
            return false;

        if (atFront)
            ApiKeys.Insert(0, key);
        else
            ApiKeys.Add(key);
        return true;
    }

    /// <summary>1 tabanlı indeksle anahtar siler.</summary>
    public bool RemoveKeyAt(int index)
    {
        if (index < 1 || index > ApiKeys.Count)
            return false;
        ApiKeys.RemoveAt(index - 1);
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        // Tek alan yerine liste yazılır; geriye dönük alan null bırakılır.
        ApiKey = null;
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, EvrenJsonContext.Default.CliConfig));
    }
}
