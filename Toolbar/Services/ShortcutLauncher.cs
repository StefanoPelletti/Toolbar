using System.Windows;
using Toolbar.ViewModels;

namespace Toolbar.Services;

// Shared launch entry point for ShortcutTile (click / context menu) and
// SearchWindow (keyboard / double-click). Consolidates the broken-path check,
// the "remove?" dialog, and the IsBroken state reset so both callers behave
// identically.
internal static class ShortcutLauncher
{
    internal static void TryLaunch(ShortcutViewModel vm, Window? owner)
    {
        if (ShortcutViewModel.IsPathBroken(vm.Path))
        {
            vm.IsBroken = true;
            OfferRemoveBroken(vm, owner);
            return;
        }

        try
        {
            vm.LaunchCommand.Execute(null);
            vm.IsBroken = false; // clear a stale broken flag on a successful launch
        }
        catch
        {
            OfferRemoveBroken(vm, owner);
        }
    }

    private static void OfferRemoveBroken(ShortcutViewModel vm, Window? owner)
    {
        var result = MessageBox.Show(
            $"\"{vm.DisplayName}\" could not be found or opened.\n\nRemove this shortcut?",
            "Shortcut unavailable",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            (owner as MainWindow)?.RemoveShortcut(vm);
    }
}
