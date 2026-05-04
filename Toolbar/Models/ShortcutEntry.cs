namespace Toolbar.Models;

public class ShortcutEntry
{
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CustomIconPath { get; set; }
}
