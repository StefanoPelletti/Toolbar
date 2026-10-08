using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
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

    // Serializes the actual file write. Timer.Change(Infinite, Infinite) does
    // not wait for an in-flight callback, so SaveImmediate could otherwise race
    // the threadpool flush and have two threads call File.WriteAllText on the
    // same path. The lock makes the second caller wait a few ms instead.
    private readonly object _flushLock = new();

    // Captured at construction (UI thread). Serialization is marshaled here so
    // the threadpool flush sees a stable AppConfig — without this, a Dictionary
    // mutation on the UI thread mid-serialize throws "Collection was modified"
    // and the save is silently lost.
    private readonly Dispatcher _uiDispatcher;

    public ConfigStore()
    {
        _uiDispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;

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
        // Mark dirty without taking _flushLock: the timer thread holds that lock
        // only for file I/O, but Save runs on the UI thread and must never block
        // on it (that was a deadlock — see Flush).
        Interlocked.Increment(ref _version);
        _debounceTimer.Change(200, System.Threading.Timeout.Infinite);
    }

    public void SaveImmediate(AppConfig config)
    {
        _debounceTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        Flush(config);
    }

    // Bumped by every Save; _flushedVersion is the version last written to disk.
    // Starts at -1 so the first flush always writes.
    private long _version;
    private long _flushedVersion = -1;

    private void Flush(AppConfig? config)
    {
        if (config is null) return;

        // Cheap idempotency: if SaveImmediate just wrote the current state and
        // the debounce callback fires right after, skip the redundant work.
        if (Interlocked.Read(ref _version) <= Interlocked.Read(ref _flushedVersion)) return;

        string json;
        long version;
        try
        {
            // Serialize on the UI thread so the AppConfig graph (Dictionary,
            // List) isn't being mutated concurrently. Invoke is a direct call
            // when we're already on the UI thread (SaveImmediate path), so it
            // adds no measurable overhead there. The version is read in the same
            // UI-thread step, so it matches exactly what was serialized.
            //
            // This must stay OUTSIDE _flushLock: holding the lock while waiting
            // for the UI thread deadlocked the app when the UI thread entered
            // Save (e.g. a monitor power-cycle moving the window) mid-flush.
            (json, version) = _uiDispatcher.Invoke(
                () => (JsonSerializer.Serialize(config, JsonOptions), Interlocked.Read(ref _version)));
        }
        catch { return; /* swallow — non-critical */ }

        lock (_flushLock)
        {
            // A concurrent flush may already have written this or a newer
            // snapshot; never overwrite newer state with older.
            if (version <= _flushedVersion) return;

            try
            {
                // File I/O stays on whichever thread Flush was called from —
                // typically the timer's threadpool thread — so a slow disk
                // never freezes the UI.
                Directory.CreateDirectory(ConfigDir);

                // Write-then-rename so a crash or power loss mid-write can never
                // leave a truncated config.json behind — Load treats a corrupt
                // file as "start fresh", which would silently wipe the user's
                // whole layout. The rename replace is atomic on NTFS.
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ConfigPath, overwrite: true);
                Interlocked.Exchange(ref _flushedVersion, version);
            }
            catch { /* swallow — non-critical */ }
        }
    }
}
