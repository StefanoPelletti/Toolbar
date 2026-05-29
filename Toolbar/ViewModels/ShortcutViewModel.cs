using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using Toolbar.Models;

namespace Toolbar.ViewModels;

public class ShortcutViewModel : ObservableBase
{
    private string _displayName = string.Empty;
    private string _path = string.Empty;
    private string? _customIconPath;
    private string? _arguments;
    private bool _runAsAdmin;
    private ImageSource? _icon;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string? CustomIconPath { get => _customIconPath; set => Set(ref _customIconPath, value); }
    public string? Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    public ICommand LaunchCommand { get; }

    public ShortcutViewModel()
    {
        LaunchCommand = new RelayCommand(Launch);
    }

    private void Launch()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(Arguments))
                psi.Arguments = Arguments;
            if (RunAsAdmin)
                psi.Verb = "runas"; // UAC elevation; user cancel throws, caught below

            Process.Start(psi);
        }
        catch { /* missing / broken shortcut, or UAC declined — ignore */ }
    }

    public ShortcutEntry ToEntry() => new()
    {
        Path = Path,
        DisplayName = DisplayName,
        CustomIconPath = CustomIconPath,
        Arguments = Arguments,
        RunAsAdmin = RunAsAdmin
    };

    public static ShortcutViewModel FromEntry(ShortcutEntry e) => new()
    {
        Path = e.Path,
        DisplayName = e.DisplayName,
        CustomIconPath = e.CustomIconPath,
        Arguments = e.Arguments,
        RunAsAdmin = e.RunAsAdmin
    };
}
