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
    private ImageSource? _icon;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string? CustomIconPath { get => _customIconPath; set => Set(ref _customIconPath, value); }
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
            Process.Start(new ProcessStartInfo
            {
                FileName = Path,
                UseShellExecute = true
            });
        }
        catch { /* silently ignore missing / broken shortcuts */ }
    }

    public ShortcutEntry ToEntry() => new()
    {
        Path = Path,
        DisplayName = DisplayName,
        CustomIconPath = CustomIconPath
    };

    public static ShortcutViewModel FromEntry(ShortcutEntry e) => new()
    {
        Path = e.Path,
        DisplayName = e.DisplayName,
        CustomIconPath = e.CustomIconPath
    };
}
