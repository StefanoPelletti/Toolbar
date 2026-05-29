namespace Toolbar.Models;

public class AppSettings
{
    public bool AlwaysOnTop    { get; set; } = false;
    public bool LaunchAtBoot   { get; set; } = true;
    public bool IsVertical     { get; set; } = false;
    public int  CrossAxisCount { get; set; } = 1;

    // Discrete 10% steps from 100% (0) to 150% (5). Stored as int so the value
    // can't drift via float round-trips and is always clamped by construction.
    public int  ScaleSteps     { get; set; } = 0;

    // Global show/hide hotkey. Gesture is "Mod+Mod+Key" (e.g. "Ctrl+Alt+Space").
    public bool   HotkeyEnabled { get; set; } = true;
    public string HotkeyGesture { get; set; } = "Ctrl+Alt+Space";
}
