using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
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

    // Cached display-layout signature for the current monitor configuration.
    // Updated when SystemEvents.DisplaySettingsChanged fires.
    private string _displaySignature = DisplayLayout.Signature();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _config = _store.Load();
        _vm.LoadFrom(_config);

        // Auto-start: refresh the registry entry every launch so a relocated
        // portable .exe self-corrects, and so new installs auto-enroll.
        AutoStartService.Apply(_vm.LaunchAtBoot);

        // One-shot migration from the legacy single-position field.
        if (_config.WindowPositions.Count == 0 &&
            (_config.Window.Left != 100 || _config.Window.Top != 100))
        {
            _config.WindowPositions[_displaySignature] = new WindowPosition
            {
                Left = _config.Window.Left,
                Top  = _config.Window.Top
            };
        }

        var pos = ResolvePositionForSignature(_displaySignature);
        Left = pos.Left;
        Top  = pos.Top;

        Topmost = _vm.AlwaysOnTop;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AlwaysOnTop))
                Topmost = _vm.AlwaysOnTop;
        };

        foreach (var shortcut in _vm.Shortcuts)
        {
            InsertTile(shortcut);
            QueueIconLoad(shortcut);
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyScale();
    }

    // OuterPanel uses LayoutTransform so its content participates in layout at the
    // scaled size — measured logical pixels stay the same, only the rendered (and
    // reported-to-parent) size grows. ApplyOrientation then bakes the same factor
    // into the fixed cross-axis dimension so the window doesn't clip its tiles.
    private void ApplyScale()
    {
        OuterPanel.LayoutTransform = new System.Windows.Media.ScaleTransform(_vm.Scale, _vm.Scale);
        ApplyOrientation(_vm.IsVertical);

        // Width/Height assigned above don't reach ActualWidth/Height until WPF's
        // next layout pass. Queue the clamp at Loaded priority so it runs after
        // that pass, otherwise we'd be clamping against the pre-scale size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ClampToVisibleArea);
    }

    private void ClampToVisibleArea()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Left, (int)Top));
        var wa = screen.WorkingArea;

        // Skip the clamp if the (scaled) window is larger than the working area —
        // Math.Clamp would otherwise see max < min and throw. The bar already
        // looks broken at that point; better to leave the user in control than
        // wedge it against an edge.
        if (ActualWidth  > wa.Width  || ActualHeight > wa.Height) return;

        Left = Math.Clamp(Left, wa.Left, wa.Right  - ActualWidth);
        Top  = Math.Clamp(Top,  wa.Top,  wa.Bottom - ActualHeight);
    }

    private WindowPosition ResolvePositionForSignature(string signature)
    {
        if (_config.WindowPositions.TryGetValue(signature, out var stored)
            && DisplayLayout.IsVisibleOn(stored.Left, stored.Top, ActualWidth, ActualHeight))
        {
            return stored;
        }

        // No exact match for this layout — reuse any previously-saved position that
        // still lands on a connected monitor, so disconnecting/rearranging displays
        // doesn't dump the window back onto the primary at (100, 100).
        foreach (var candidate in _config.WindowPositions.Values)
        {
            if (DisplayLayout.IsVisibleOn(candidate.Left, candidate.Top, ActualWidth, ActualHeight))
            {
                var reused = new WindowPosition { Left = candidate.Left, Top = candidate.Top };
                _config.WindowPositions[signature] = reused;
                _store.Save(_config);
                return reused;
            }
        }

        var (dl, dt) = DisplayLayout.DefaultPosition();
        var fresh = new WindowPosition { Left = dl, Top = dt };
        _config.WindowPositions[signature] = fresh;
        _store.Save(_config);
        return fresh;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Capture current position under the OLD signature first, so returning to
            // that layout later restores what the user had.
            _config.WindowPositions[_displaySignature] = new WindowPosition
            {
                Left = Left,
                Top  = Top
            };

            _displaySignature = DisplayLayout.Signature();
            var pos = ResolvePositionForSignature(_displaySignature);
            Left = pos.Left;
            Top  = pos.Top;
            _store.Save(_config);
        });
    }

    // ── Orientation ──────────────────────────────────────────────────────────

    internal void ApplyOrientation(bool vertical)
    {
        // Clamp for rendering only — never write back. The user's chosen
        // CrossAxisCount stays in the VM intact, so deleting shortcuts down to
        // fewer than that count and re-adding them later restores the original
        // layout instead of leaving it stuck at the smaller value.
        int maxCount = Math.Max(1, _vm.Shortcuts.Count);
        int n = Math.Clamp(_vm.CrossAxisCount, 1, maxCount);

        SizeToContent = SizeToContent.Manual;

        // OuterPanel's host margins (8 px on the cross axis) scale with the
        // LayoutTransform; the outer Border (1 px each side) does not.
        double scaled = (n * TileStep + 8) * _vm.Scale + 2;

        if (vertical)
        {
            // Fixed width derived from column count; height auto-sizes
            Width  = scaled;
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
            Height = scaled;
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
        // The grip lives outside OuterPanel's LayoutTransform, so its mouse delta
        // is in window pixels — divide by the scaled tile width to get columns.
        double step = TileStep * _vm.Scale;
        int newCount = Math.Clamp(
            _resizeStartCount + (int)Math.Round(delta / step),
            1, maxCount);

        if (newCount == _vm.CrossAxisCount) return;

        _vm.CrossAxisCount = newCount;
        ApplyOrientation(_vm.IsVertical);

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
        var tile = new ShortcutTile { DataContext = shortcut };
        tile.Margin = new Thickness(2); // uniform — works for both wrap directions

        tile.AllowDrop = true;
        tile.Drop     += OnTile_Drop;
        tile.DragOver += OnTile_DragOver;

        int insertAt = atIndex ?? ShortcutsHost.Children.Count;
        ShortcutsHost.Children.Insert(insertAt, tile);
    }

    // Icon extraction can take 50-200 ms per shortcut (COM + shell I/O, possibly
    // network for .lnk targets), so doing it inline in InsertTile would block
    // the constructor for seconds when the user has many shortcuts. Posting at
    // Background priority lets WPF paint and process input between extractions;
    // each loaded icon flows back to the bound Image via ShortcutViewModel.Icon.
    private void QueueIconLoad(ShortcutViewModel shortcut)
    {
        if (shortcut.Icon is not null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (shortcut.Icon is not null) return;
            // Skip the 50-200 ms shell/COM call if the user removed the
            // shortcut while this Background-priority work was queued.
            if (!_vm.Shortcuts.Contains(shortcut)) return;
            shortcut.Icon = shortcut.CustomIconPath is not null
                ? IconExtractor.FromFile(shortcut.CustomIconPath)
                : IconExtractor.FromPath(shortcut.Path);
        });
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
        QueueIconLoad(shortcut);
        PersistShortcuts();
    }

    internal void RemoveShortcut(ShortcutViewModel shortcut)
    {
        var tile = ShortcutsHost.Children.OfType<ShortcutTile>()
                                         .FirstOrDefault(t => t.DataContext == shortcut);
        if (tile is not null) ShortcutsHost.Children.Remove(tile);

        _vm.Shortcuts.Remove(shortcut);

        // Re-layout in case we removed the last tile in a column/row. The VM's
        // CrossAxisCount is left alone — ApplyOrientation clamps for rendering
        // only, so re-adding shortcuts later restores the preferred layout.
        ApplyOrientation(_vm.IsVertical);

        PersistShortcuts();
    }

    private List<ShortcutTile> Tiles =>
        ShortcutsHost.Children.OfType<ShortcutTile>().ToList();

    private ShortcutTile? _dragOverTile;

    // Called by ShortcutTile once DoDragDrop returns, so a drag that ends without
    // landing on a tile (drop cancelled or off-toolbar) still releases the
    // hover-scale on whichever tile was last dragged over.
    internal void ClearTileDragOver()
    {
        _dragOverTile?.EndDragOver();
        _dragOverTile = null;
    }

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
            ApplyScale();
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
        if (!_config.WindowPositions.TryGetValue(_displaySignature, out var entry))
        {
            entry = new WindowPosition();
            _config.WindowPositions[_displaySignature] = entry;
        }
        entry.Left = Left;
        entry.Top  = Top;
        _store.Save(_config);
    }

    internal void PersistShortcuts()
    {
        _vm.ApplyTo(_config);
        _store.Save(_config);
    }
}
