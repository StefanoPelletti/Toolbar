using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Toolbar.ViewModels;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Toolbar.Controls;

public partial class ShortcutTile : UserControl
{
    private ShortcutViewModel Vm => (ShortcutViewModel)DataContext;
    private MainWindow? ParentWindow => Window.GetWindow(this) as MainWindow;

    private bool _dragOccurred;
    private Point _dragStart;

    public ShortcutTile()
    {
        InitializeComponent();
    }

    // ── Hover highlight ──────────────────────────────────────────────────────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Bd.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TileHover"];
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Bd.Background = System.Windows.Media.Brushes.Transparent;
    }

    // ── Left-click: launch ──────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragOccurred = false;
        _dragStart = e.GetPosition(this);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragOccurred) { _dragOccurred = false; return; }
        Vm.LaunchCommand.Execute(null);
        e.Handled = true;
    }

    // ── Drag for reorder ────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        var diff = pos - _dragStart;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragOccurred = true;
        DragDrop.DoDragDrop(this, Vm, DragDropEffects.Move);
        e.Handled = true;
    }

    // ── Context menu ────────────────────────────────────────────────────────

    private void OnContextLaunch(object sender, RoutedEventArgs e) =>
        Vm.LaunchCommand.Execute(null);

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
