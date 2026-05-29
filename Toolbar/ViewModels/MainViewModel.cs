using System.Collections.ObjectModel;
using Toolbar.Models;

namespace Toolbar.ViewModels;

public class MainViewModel : ObservableBase
{
    private bool   _alwaysOnTop;
    private bool   _launchAtBoot;
    private bool   _isVertical;
    private int    _crossAxisCount = 1;
    private int    _scaleSteps;
    private bool   _hotkeyEnabled = true;
    private string _hotkeyGesture = "Ctrl+Alt+Space";

    public ObservableCollection<ShortcutViewModel> Shortcuts { get; } = [];

    public bool AlwaysOnTop    { get => _alwaysOnTop;    set => Set(ref _alwaysOnTop,    value); }
    public bool LaunchAtBoot   { get => _launchAtBoot;   set => Set(ref _launchAtBoot,   value); }
    public bool IsVertical     { get => _isVertical;     set => Set(ref _isVertical,     value); }
    public int  CrossAxisCount { get => _crossAxisCount; set => Set(ref _crossAxisCount, Math.Max(1, value)); }

    public bool   HotkeyEnabled { get => _hotkeyEnabled; set => Set(ref _hotkeyEnabled, value); }
    public string HotkeyGesture { get => _hotkeyGesture; set => Set(ref _hotkeyGesture, value); }

    public int  ScaleSteps
    {
        get => _scaleSteps;
        set
        {
            var clamped = Math.Clamp(value, 0, 5);
            if (_scaleSteps == clamped) return;
            _scaleSteps = clamped;
            Notify(nameof(ScaleSteps));
            Notify(nameof(Scale));
        }
    }

    public double Scale => 1.0 + _scaleSteps * 0.1;

    public void LoadFrom(AppConfig config)
    {
        AlwaysOnTop    = config.Settings.AlwaysOnTop;
        LaunchAtBoot   = config.Settings.LaunchAtBoot;
        IsVertical     = config.Settings.IsVertical;
        CrossAxisCount = config.Settings.CrossAxisCount;
        ScaleSteps     = config.Settings.ScaleSteps;
        HotkeyEnabled  = config.Settings.HotkeyEnabled;
        HotkeyGesture  = config.Settings.HotkeyGesture;

        Shortcuts.Clear();
        foreach (var entry in config.Shortcuts)
            Shortcuts.Add(ShortcutViewModel.FromEntry(entry));
    }

    public void ApplyTo(AppConfig config)
    {
        config.Settings.AlwaysOnTop    = AlwaysOnTop;
        config.Settings.LaunchAtBoot   = LaunchAtBoot;
        config.Settings.IsVertical     = IsVertical;
        config.Settings.CrossAxisCount = CrossAxisCount;
        config.Settings.ScaleSteps     = ScaleSteps;
        config.Settings.HotkeyEnabled  = HotkeyEnabled;
        config.Settings.HotkeyGesture  = HotkeyGesture;
        config.Shortcuts = Shortcuts.Select(s => s.ToEntry()).ToList();
    }
}
