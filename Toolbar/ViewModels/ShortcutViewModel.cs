using System.Diagnostics;
using System.IO;
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
    private bool _isBroken;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public string? CustomIconPath { get => _customIconPath; set => Set(ref _customIconPath, value); }
    public string? Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    // True when the referenced program/file no longer exists (e.g. it was
    // uninstalled or deleted). The tile binds to this to render a visible
    // "broken" state instead of vanishing into an empty, transparent tile.
    public bool IsBroken { get => _isBroken; set => Set(ref _isBroken, value); }

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
            // runas only applies to executables and shortcuts; applying it to a
        // folder or document either throws or silently opens with unexpected
        // elevation, giving the user no feedback (C3).
        if (RunAsAdmin &&
            (Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
             Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            psi.Verb = "runas";

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

    // Shared by the launch path and the icon-load path so both agree on what
    // counts as "broken". Mirrors the checks the shell itself would fail on.
    // Pass a pre-resolved resolvedLnkTarget to avoid a second COM call when
    // the caller has already resolved the .lnk (P3).
    public static bool IsPathBroken(string path, string? resolvedLnkTarget = null)
    {
        if (string.IsNullOrEmpty(path)) return true;

        // Virtual shell items (Recycle Bin etc.) have no file-system path and
        // are always valid.
        if (path.StartsWith("::")) return false;

        if (!File.Exists(path) && !Directory.Exists(path))
            return true;

        // A .lnk whose file still exists but whose target was uninstalled is
        // also broken — Process.Start would only surface the shell's own
        // "missing shortcut" dialog, so detect the dead target ourselves.
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = resolvedLnkTarget ?? Services.IconExtractor.ResolveLnkTarget(path);
            if (!string.IsNullOrEmpty(target)
                && !File.Exists(target) && !Directory.Exists(target))
                return true;
        }

        return false;
    }

    public static ShortcutViewModel FromEntry(ShortcutEntry e) => new()
    {
        Path = e.Path,
        DisplayName = e.DisplayName,
        CustomIconPath = e.CustomIconPath,
        Arguments = e.Arguments,
        RunAsAdmin = e.RunAsAdmin
    };
}
