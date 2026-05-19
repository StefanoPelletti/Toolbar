using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Toolbar.ViewModels;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Toolbar.Controls;

public partial class ShortcutTile : UserControl
{
    private ShortcutViewModel Vm => (ShortcutViewModel)DataContext;
    private MainWindow? ParentWindow => Window.GetWindow(this) as MainWindow;

    // Frozen so they can be reused safely across all tiles without per-animation
    // allocation. DoubleAnimation itself still has to be new per call (it carries
    // the target value), but the easing function is the only stateless piece.
    private static readonly CubicEase EaseInFn  = CreateFrozen(EasingMode.EaseIn);
    private static readonly CubicEase EaseOutFn = CreateFrozen(EasingMode.EaseOut);

    private static CubicEase CreateFrozen(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    private bool _dragOccurred;
    private Point _dragStart;

    public ShortcutTile()
    {
        InitializeComponent();
    }

    // ── Scale animation helper ───────────────────────────────────────────────

    private void AnimateScale(double to, double ms = 120,
        EasingMode easing = EasingMode.EaseOut)
    {
        var fn  = easing == EasingMode.EaseIn ? EaseInFn : EaseOutFn;
        var dur = TimeSpan.FromMilliseconds(ms);
        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, dur) { EasingFunction = fn });
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(to, dur) { EasingFunction = fn });
    }

    // Called by MainWindow to mark this tile as the current drop target
    internal void BeginDragOver() => AnimateScale(1.18, 110);
    internal void EndDragOver()   => AnimateScale(1.0,  160);

    // ── Hover highlight ──────────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Bd.Background = (System.Windows.Media.Brush)
            System.Windows.Application.Current.Resources["TileHover"];
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Bd.Background = System.Windows.Media.Brushes.Transparent;
        AnimateScale(1.0, 150); // spring back if mouse leaves while pressed
    }

    // ── Launch with broken-shortcut detection ────────────────────────────────

    private void TryLaunch()
    {
        var path = Vm.Path;

        bool isShellItem = path.StartsWith("::");
        if (!isShellItem && !File.Exists(path) && !Directory.Exists(path))
        {
            OfferRemoveBroken();
            return;
        }

        // A .lnk whose file still exists but whose target was uninstalled would
        // otherwise hit Process.Start, where the shell shows its own "missing
        // shortcut" dialog and our try/catch never sees a failure. Check the
        // resolved target here so we can offer to remove the dead entry instead.
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = Services.IconExtractor.ResolveLnkTarget(path);
            if (!string.IsNullOrEmpty(target)
                && !File.Exists(target) && !Directory.Exists(target))
            {
                OfferRemoveBroken();
                return;
            }
        }

        try { Vm.LaunchCommand.Execute(null); }
        catch  { OfferRemoveBroken(); }
    }

    private void OfferRemoveBroken()
    {
        var result = MessageBox.Show(
            $"\"{Vm.DisplayName}\" could not be found or opened.\n\nRemove this shortcut?",
            "Shortcut unavailable",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
            ParentWindow?.RemoveShortcut(Vm);
    }

    // ── Left-click: press animation + launch ────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragOccurred = false;
        _dragStart = e.GetPosition(this);
        AnimateScale(0.85, 100, EasingMode.EaseIn); // press down
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        AnimateScale(1.0, 180); // spring back
        if (_dragOccurred) { _dragOccurred = false; return; }
        TryLaunch();
        e.Handled = true;
    }

    // ── Drag for reorder ────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos  = e.GetPosition(this);
        var diff = pos - _dragStart;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragOccurred = true;

        // Tile looks "picked up" while dragging
        Opacity = 0.45;
        AnimateScale(0.80, 120);

        DragDrop.DoDragDrop(this, Vm, DragDropEffects.Move); // blocks until drop

        // Restore once drag finishes
        Opacity = 1.0;
        AnimateScale(1.0, 200);

        // OnTile_Drop already clears the hovered tile when a drop lands on a tile;
        // this covers the cancelled-or-dropped-outside case.
        ParentWindow?.ClearTileDragOver();

        e.Handled = true;
    }

    // ── Context menu ────────────────────────────────────────────────────────

    private void OnContextLaunch(object sender, RoutedEventArgs e) => TryLaunch();

    private void OnContextRename(object sender, RoutedEventArgs e)
    {
        var dlg = new RenameDialog(Vm.DisplayName) { Owner = ParentWindow };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
        {
            Vm.DisplayName = dlg.Result;
            ParentWindow?.PersistShortcuts();
        }
    }

    private void OnContextChangeIcon(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose icon",
            Filter = "Icon files|*.ico;*.png|All files|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() != true) return;

        Vm.CustomIconPath = dlg.FileName;
        Vm.Icon = Services.IconExtractor.FromFile(dlg.FileName);
        ParentWindow?.PersistShortcuts();
    }

    private void OnContextRemove(object sender, RoutedEventArgs e) =>
        ParentWindow?.RemoveShortcut(Vm);
}
