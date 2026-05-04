using System.Collections.ObjectModel;
using Toolbar.Models;

namespace Toolbar.ViewModels;

public class MainViewModel : ObservableBase
{
    private bool _alwaysOnTop;
    private bool _launchAtBoot;
    private bool _isVertical;

    public ObservableCollection<ShortcutViewModel> Shortcuts { get; } = [];

    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }
    public bool LaunchAtBoot { get => _launchAtBoot; set => Set(ref _launchAtBoot, value); }
    public bool IsVertical { get => _isVertical; set => Set(ref _isVertical, value); }

    public void LoadFrom(AppConfig config)
    {
        AlwaysOnTop = config.Settings.AlwaysOnTop;
        LaunchAtBoot = config.Settings.LaunchAtBoot;
        IsVertical = config.Settings.IsVertical;

        Shortcuts.Clear();
        foreach (var entry in config.Shortcuts)
            Shortcuts.Add(ShortcutViewModel.FromEntry(entry));
    }

    public void ApplyTo(AppConfig config)
    {
        config.Settings.AlwaysOnTop = AlwaysOnTop;
        config.Settings.LaunchAtBoot = LaunchAtBoot;
        config.Settings.IsVertical = IsVertical;
        config.Shortcuts = Shortcuts.Select(s => s.ToEntry()).ToList();
    }
}
