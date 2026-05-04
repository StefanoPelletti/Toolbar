using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbar.Models;

namespace Toolbar.Services;

public class ConfigStore
{
    private static readonly string ConfigDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toolbar");

    private static readonly string ConfigPath =
        System.IO.Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private System.Threading.Timer? _debounceTimer;
    private AppConfig? _pending;

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch { /* corrupt config — start fresh */ }

        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        _pending = config;
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(_ => Flush(_pending), null, 200, System.Threading.Timeout.Infinite);
    }

    public void SaveImmediate(AppConfig config)
    {
        _debounceTimer?.Dispose();
        Flush(config);
    }

    private void Flush(AppConfig? config)
    {
        if (config is null) return;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* swallow — non-critical */ }
    }
}
