using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // ── Launch with broken-shortcut detection ────────────────────────────────

    private void TryLaunch()
    {
        var path = Vm.Path;

        // Shell special items (e.g. "::{CLSID}") are not on the file system — skip existence check
        bool isShellItem = path.StartsWith("::");
        if (!isShellItem && !File.Exists(path) && !Directory.Exists(path))
        {
            OfferRemoveBroken();
            return;
        }

        try
        {
            Vm.LaunchCommand.Execute(null);
        }
        catch
        {
            OfferRemoveBroken();
        }
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
        TryLaunch();
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
