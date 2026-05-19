namespace Toolbar.Models;

public class WindowPosition
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;

    // Used to pick the most-recently-used fallback position when reopening
    // under a layout that has no exact saved match. Older configs deserialize
    // with DateTime.MinValue, which sorts last — fine, the active entry gets
    // refreshed on first save.
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

public class AppConfig
{
    // Legacy single-position field. Kept for one-shot migration into WindowPositions
    // when loading a config written by a pre-multi-monitor build.
    public WindowPosition Window { get; set; } = new();

    public Dictionary<string, WindowPosition> WindowPositions { get; set; } = new();

    public AppSettings Settings { get; set; } = new();
    public List<ShortcutEntry> Shortcuts { get; set; } = [];
}
