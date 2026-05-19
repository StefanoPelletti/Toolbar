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

    // Single long-lived timer reset via Change() per Save call. The old
    // dispose+new-per-Save pattern allocated a Timer for every pixel of window
    // movement (OnLocationChanged → Save), which produced unnecessary GC churn
    // during drag.
    private readonly System.Threading.Timer _debounceTimer;
    private AppConfig? _pending;

    public ConfigStore()
    {
        _debounceTimer = new System.Threading.Timer(
            _ => Flush(_pending),
            null,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);
    }

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
        _debounceTimer.Change(200, System.Threading.Timeout.Infinite);
    }

    public void SaveImmediate(AppConfig config)
    {
        _debounceTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
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
