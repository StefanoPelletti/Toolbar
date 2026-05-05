namespace Toolbar.Models;

public class AppSettings
{
    public bool AlwaysOnTop    { get; set; } = false;
    public bool LaunchAtBoot   { get; set; } = false;
    public bool IsVertical     { get; set; } = false;
    public int  CrossAxisCount { get; set; } = 1;
}
