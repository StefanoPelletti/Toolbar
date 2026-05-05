using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Toolbar.Controls;
using Toolbar.Models;
using Toolbar.Services;
using Toolbar.ViewModels;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;

namespace Toolbar;

public partial class MainWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly MainViewModel _vm = new();

    // Each tile occupies this many pixels in the cross-axis direction
    // (tile 48 + 2px margin each side = 52)
    private const int TileStep = 52;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _config = _store.Load();
        _vm.LoadFrom(_config);

        Left = _config.Window.Left;
        Top = _config.Window.Top;

        Topmost = _vm.AlwaysOnTop;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AlwaysOnTop))
                Topmost = _vm.AlwaysOnTop;
        };

        foreach (var shortcut in _vm.Shortcuts)
            InsertTile(shortcut);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyOrientation(_vm.IsVertical);

        var screen = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, screen.Left, screen.Right - ActualWidth);
        Top  = Math.Clamp(Top,  screen.Top,  screen.Bottom - ActualHeight);
    }

    // ── Orientation ──────────────────────────────────────────────────────────

    internal void ApplyOrientation(bool vertical)
    {
        // Clamp CrossAxisCount to valid range for current tile count
        int maxCount = Math.Max(1, _vm.Shortcuts.Count);
        _vm.CrossAxisCount = Math.Clamp(_vm.CrossAxisCount, 1, maxCount);
        int n = _vm.CrossAxisCount;

        SizeToContent = SizeToContent.Manual;

        if (vertical)
        {
            // Fixed width derived from column count; height auto-sizes
            Width  = n * TileStep + 10;   // 10 = host margins (4+4) + border (1+1)
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;

            OuterPanel.Orientation    = Orientation.Vertical;
            ShortcutsHost.Orientation = Orientation.Horizontal; // tiles wrap left→right
            ButtonsHost.Orientation   = Orientation.Vertical;

            ShortcutsHost.Margin = new Thickness(4, 4, 4, 2);
            ButtonsHost.Margin   = new Thickness(4, 2, 4, 6);

            // Menu button on top, tiles below
            OuterPanel.Children.Clear();
            OuterPanel.Children.Add(ButtonsHost);
            OuterPanel.Children.Add(ShortcutsHost);

            // Grip on the right edge — drag right = more columns, drag left = fewer
            ResizeGrip.Width               = 8;
            ResizeGrip.Height              = double.NaN;
            ResizeGrip.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            ResizeGrip.VerticalAlignment   = VerticalAlignment.Stretch;
            ResizeGrip.Cursor              = Cursors.SizeWE;
            GripDots.Orientation           = Orientation.Vertical;
        }
        else
        {
            // Fixed height derived from row count; width auto-sizes
            Height = n * TileStep + 10;
            Width  = double.NaN;
            SizeToContent = SizeToContent.Width;

            OuterPanel.Orientation    = Orientation.Horizontal;
            ShortcutsHost.Orientation = Orientation.Vertical;  // tiles wrap top→bottom
            ButtonsHost.Orientation   = Orientation.Horizontal;

            ShortcutsHost.Margin = new Thickness(6, 4, 4, 4);
            ButtonsHost.Margin   = new Thickness(4, 4, 6, 4);

            // Tiles on the left, menu button on the right
            OuterPanel.Children.Clear();
            OuterPanel.Children.Add(ShortcutsHost);
            OuterPanel.Children.Add(ButtonsHost);

            // Grip on the bottom edge — drag down = more rows, drag up = fewer
            ResizeGrip.Height              = 8;
            ResizeGrip.Width               = double.NaN;
            ResizeGrip.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            ResizeGrip.VerticalAlignment   = VerticalAlignment.Bottom;
            ResizeGrip.Cursor              = Cursors.SizeNS;
            GripDots.Orientation           = Orientation.Horizontal;
        }

        // Uniform tile margin so tiles look right in both wrap directions
        foreach (var tile in Tiles)
            tile.Margin = new Thickness(2);
    }

    // ── Resize grip ──────────────────────────────────────────────────────────

    private bool  _isResizing;
    private Point _resizeStart;
    private int   _resizeStartCount;

    private void OnGrip_Down(object sender, MouseButtonEventArgs e)
    {
        _isResizing      = true;
        _resizeStart     = e.GetPosition(this);
        _resizeStartCount = _vm.CrossAxisCount;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGrip_Move(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        var pos   = e.GetPosition(this);
        double delta = _vm.IsVertical
            ? pos.X - _resizeStart.X   // horizontal drag → columns
            : pos.Y - _resizeStart.Y;  // vertical drag   → rows

        int maxCount = Math.Max(1, _vm.Shortcuts.Count);
        int newCount = Math.Clamp(
            _resizeStartCount + (int)Math.Round(delta / TileStep),
            1, maxCount);

        if (newCount == _vm.CrossAxisCount) return;

        _vm.CrossAxisCount = newCount;
        ApplyOrientation(_vm.IsVertical);

        // Keep the window anchored: adjust position so the opposite edge stays still
        if (_vm.IsVertical)
            Left = Left; // width grows rightward — no adjustment needed
        // In horizontal mode height grows downward — no adjustment needed either
    }

    private void OnGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        ResizeGrip.ReleaseMouseCapture();
        PersistShortcuts();
        e.Handled = true;
    }

    // ── Tile management ─────────────────────────────────────────────────────

    private void InsertTile(ShortcutViewModel shortcut, int? atIndex = null)
    {
        if (shortcut.Icon is null)
        {
            shortcut.Icon = shortcut.CustomIconPath is not null
                ? IconExtractor.FromFile(shortcut.CustomIconPath)
                : IconExtractor.FromPath(shortcut.Path);
        }

        var tile = new ShortcutTile { DataContext = shortcut };
        tile.Margin = new Thickness(2); // uniform — works for both wrap directions

        tile.AllowDrop = true;
        tile.Drop     += OnTile_Drop;
        tile.DragOver += OnTile_DragOver;

        int insertAt = atIndex ?? ShortcutsHost.Children.Count;
        ShortcutsHost.Children.Insert(insertAt, tile);
    }

    private void AddShortcut(string path)
    {
        if (_vm.Shortcuts.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        string name;
        if (Directory.Exists(path))
        {
            name = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = path;
        }
        else
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            name = ext is ".exe" or ".lnk"
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileName(path);
        }

        var shortcut = new ShortcutViewModel { Path = path, DisplayName = name };
        _vm.Shortcuts.Add(shortcut);
        InsertTile(shortcut);
        PersistShortcuts();
    }

    internal void RemoveShortcut(ShortcutViewModel shortcut)
    {
        var tile = ShortcutsHost.Children.OfType<ShortcutTile>()
                                         .FirstOrDefault(t => t.DataContext == shortcut);
        if (tile is not null) ShortcutsHost.Children.Remove(tile);

        _vm.Shortcuts.Remove(shortcut);

        // Clamp cross-axis count in case we removed the last tile in a column/row
        int maxCount = Math.Max(1, _vm.Shortcuts.Count);
        if (_vm.CrossAxisCount > maxCount)
        {
            _vm.CrossAxisCount = maxCount;
            ApplyOrientation(_vm.IsVertical);
        }

        PersistShortcuts();
    }

    private List<ShortcutTile> Tiles =>
        ShortcutsHost.Children.OfType<ShortcutTile>().ToList();

    private ShortcutTile? _dragOverTile;

    // ── Drag-drop: files / folders onto bar ─────────────────────────────────

    private void OnWindow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
            AddShortcut(f);
    }

    // ── Drag-drop: tile reorder ──────────────────────────────────────────────

    private void OnTile_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ShortcutViewModel))) return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        if (sender is ShortcutTile tile && tile != _dragOverTile)
        {
            _dragOverTile?.EndDragOver();
            _dragOverTile = tile;
            tile.BeginDragOver();
        }
    }

    private void OnTile_Drop(object sender, DragEventArgs e)
    {
        _dragOverTile?.EndDragOver();
        _dragOverTile = null;

        if (!e.Data.GetDataPresent(typeof(ShortcutViewModel))) return;
        if (sender is not ShortcutTile targetTile) return;

        var dragged = (ShortcutViewModel)e.Data.GetData(typeof(ShortcutViewModel));
        var target  = (ShortcutViewModel)targetTile.DataContext;

        if (dragged == target) return;

        var sourceTile = Tiles.FirstOrDefault(t => t.DataContext == dragged);
        if (sourceTile is null) return;

        int fromIndex = ShortcutsHost.Children.IndexOf(sourceTile);
        int toIndex   = ShortcutsHost.Children.IndexOf(targetTile);
        int vmFrom    = _vm.Shortcuts.IndexOf(dragged);
        int vmTo      = _vm.Shortcuts.IndexOf(target);

        if (fromIndex < 0 || toIndex < 0 || vmFrom < 0 || vmTo < 0) return;

        ShortcutsHost.Children.RemoveAt(fromIndex);
        ShortcutsHost.Children.Insert(toIndex, sourceTile);
        _vm.Shortcuts.Move(vmFrom, vmTo);

        PersistShortcuts();
        e.Handled = true;
    }

    // ── Window events ────────────────────────────────────────────────────────

    private void OnDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source is ShortcutTile || e.Source is Button) return;
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // ── Menu button ──────────────────────────────────────────────────────────

    private void OnMenu_Click(object sender, RoutedEventArgs e)
    {
        MenuBtn.ContextMenu.PlacementTarget = MenuBtn;
        MenuBtn.ContextMenu.Placement = _vm.IsVertical ? PlacementMode.Left : PlacementMode.Bottom;
        MenuBtn.ContextMenu.IsOpen = true;
    }

    private void OnAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Add shortcut — pick any file",
            Filter      = "All files|*.*|Applications|*.exe|Shortcuts|*.lnk",
            FilterIndex = 1,
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
            AddShortcut(dlg.FileName);
    }

    private void OnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select a folder to add as a shortcut",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            AddShortcut(dlg.SelectedPath);
    }

    private void OnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    internal void OpenSettings()
    {
        var dlg = new SettingsWindow(_vm) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ApplyOrientation(_vm.IsVertical);
            PersistShortcuts();
        }
    }

    private void OnMinimize_Click(object sender, RoutedEventArgs e) => Hide();

    private void OnClose_Click(object sender, RoutedEventArgs e)
    {
        _store.SaveImmediate(_config);
        Close();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _config.Window.Left = Left;
        _config.Window.Top  = Top;
        _store.Save(_config);
    }

    internal void PersistShortcuts()
    {
        _vm.ApplyTo(_config);
        _store.Save(_config);
    }
}
