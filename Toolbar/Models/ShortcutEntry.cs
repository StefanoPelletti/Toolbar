namespace Toolbar.Models;

public class ShortcutEntry
{
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CustomIconPath { get; set; }

    // Optional command-line arguments passed on launch.
    public string? Arguments { get; set; }

    // Launch elevated (triggers UAC). Older configs deserialize this as false.
    public bool RunAsAdmin { get; set; }
}
