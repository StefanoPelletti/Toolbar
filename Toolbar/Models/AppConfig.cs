namespace Toolbar.Models;

public class WindowPosition
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
}

public class AppConfig
{
    public WindowPosition Window { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<ShortcutEntry> Shortcuts { get; set; } = [];
}
