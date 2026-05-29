using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    private readonly HotKeyService _hotkey = new();

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
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hotkey.Dispose();
            // Flush any debounced save synchronously. Exiting via the tray's
            // "Exit" item goes straight to Shutdown() without the menu Close
            // path, so without this the last move/reorder in the 200 ms debounce
            // window would be lost when the threadpool timer never fires.
            PersistImmediate();
        };

        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The HWND only exists from here on, which is what RegisterHotKey needs.
        _hotkey.Attach((HwndSource)PresentationSource.FromVisual(this)!);
        _hotkey.Pressed += ToggleVisibility;
        ApplyHotkey();
    }

    // Re-registers (or clears) the global hotkey from the current VM settings.
    // Called at startup and whenever Settings is saved.
    internal void ApplyHotkey()
    {
        if (_vm.HotkeyEnabled)
            _hotkey.Register(_vm.HotkeyGesture);
        else
            _hotkey.Unregister();
    }

    // Summon-or-dismiss. Pressing the hotkey while the bar is up and focused tucks
    // it away; otherwise it surfaces and takes focus.
    private void ToggleVisibility()
    {
        if (IsVisible && IsActive)
        {
            _searchWindow?.Close();
            Hide();
        }
        else
        {
            Show();
            Activate();
            ShowSearch();
        }
    }

    // ── Quick-launch palette ──────────────────────────────────────────────────

    private SearchWindow? _searchWindow;

    private void OnSearch_Click(object sender, RoutedEventArgs e) => ShowSearch();

    private void ShowSearch()
    {
        if (_vm.Shortcuts.Count == 0) return;
        if (_searchWindow is not null) { _searchWindow.Activate(); return; }

        // Make sure the bar is on-screen behind the palette when auto-hidden.
        if (_autoHide && !_revealed) Reveal();

        var win = new SearchWindow(_vm.Shortcuts) { Owner = this };
        win.PositionNear(this);
        win.Closed += (_, _) =>
        {
            _searchWindow = null;
            // Palette gone — let an auto-hidden bar tuck away again.
            if (_autoHide && !IsMouseOver && !IsActive) ScheduleConceal();
        };
        _searchWindow = win;
        win.Show();
        win.Activate();
    }

    // ── Auto-hide / edge docking ──────────────────────────────────────────────

    private enum Edge { Left, Right, Top, Bottom }

    private const double RevealStrip = 4.0; // px left peeking when concealed

    private const double SnapThreshold = 28.0; // px from an edge that counts as "docked"

    private bool  _autoHide;   // setting is on
    private bool  _docked;     // currently snapped to an edge (only then does it hide)
    private bool  _revealed;   // currently fully on-screen (vs tucked away)
    private bool  _sliding;    // a programmatic move is in flight — suppress persist
    private Edge  _edge;
    private Point _anchor;     // the docked, fully-visible position
    private DispatcherTimer? _slideTimer;
    private DispatcherTimer? _concealTimer;

    // Single entry point for every auto-hide state change: turning the feature on
    // or off, and re-docking after a scale/orientation change. When the feature is
    // off it falls through to the ordinary visible-area clamp, so it can stand in
    // for ClampToVisibleArea as the post-layout step.
    internal void RefreshAutoHide()
    {
        bool want = _vm.AutoHide;

        if (want && !_autoHide)            // turning on
        {
            _autoHide = true;
            _revealed = true;
            // Respect where the bar already sits: dock+hide only if it's at an
            // edge, otherwise leave it floating and visible.
            EvaluateDockAfterDrag();
        }
        else if (!want && _autoHide)       // turning off → release and stay put
        {
            _autoHide = false;
            _docked = false;
            CancelConceal();
            StopSlide();
            _revealed = true;
            SetWindowPosition(_anchor);
            PersistPosition(_anchor.X, _anchor.Y);
        }
        else if (want && _autoHide)        // already on; geometry changed
        {
            if (_docked)
            {
                ComputeEdgeAndSnapAnchor();
                SetWindowPosition(_revealed ? _anchor : HiddenPosition());
            }
            else
            {
                ClampToVisibleArea();
            }
        }
        else                               // off, and was off — normal behaviour
        {
            ClampToVisibleArea();
        }
    }

    // Called after the user finishes dragging the bar (auto-hide on). If they let
    // go near an eligible edge it docks (and will hide); otherwise it undocks and
    // stays put, fully visible. This is what makes docking "magnetic" and stops a
    // floating bar from sliding off to a stale edge.
    private void EvaluateDockAfterDrag()
    {
        if (!_autoHide) return;

        var wa = ScreenWorkingAreaAt(new Point(Left, Top));
        double w = ActualWidth, h = ActualHeight;

        Edge? near = null;
        double best = SnapThreshold;

        // Only the two edges that suit the current orientation are eligible.
        if (_vm.IsVertical)
        {
            double dLeft  = Math.Abs(Left - wa.Left);
            double dRight = Math.Abs(wa.Right - (Left + w));
            if (dLeft  <= best) { best = dLeft;  near = Edge.Left; }
            if (dRight <= best) { best = dRight; near = Edge.Right; }
        }
        else
        {
            double dTop    = Math.Abs(Top - wa.Top);
            double dBottom = Math.Abs(wa.Bottom - (Top + h));
            if (dTop    <= best) { best = dTop;    near = Edge.Top; }
            if (dBottom <= best) { best = dBottom; near = Edge.Bottom; }
        }

        _anchor = new Point(Left, Top);

        if (near is Edge edge)
        {
            _docked = true;
            _edge = edge;
            SnapAnchorFlush();
            _revealed = true;
            SetWindowPosition(_anchor);
            PersistPosition(_anchor.X, _anchor.Y);
            ScheduleConceal();
        }
        else
        {
            // Floating freely — no hiding.
            _docked = false;
            _revealed = true;
            CancelConceal();
            PersistPosition(_anchor.X, _anchor.Y);
        }
    }

    // Pull the anchor flush to the current _edge on the cross axis, keeping its
    // position along the edge (clamped into the working area).
    private void SnapAnchorFlush()
    {
        var wa = ScreenWorkingAreaAt(_anchor);
        double w = ActualWidth, h = ActualHeight;

        switch (_edge)
        {
            case Edge.Left:   _anchor.X = wa.Left;          break;
            case Edge.Right:  _anchor.X = wa.Right  - w;    break;
            case Edge.Top:    _anchor.Y = wa.Top;           break;
            case Edge.Bottom: _anchor.Y = wa.Bottom - h;    break;
        }
        _anchor.X = Math.Clamp(_anchor.X, wa.Left, Math.Max(wa.Left, wa.Right  - w));
        _anchor.Y = Math.Clamp(_anchor.Y, wa.Top,  Math.Max(wa.Top,  wa.Bottom - h));
    }

    // Picks the edge to dock against from the bar's orientation and which side it
    // sits nearest, then snaps the anchor flush to that edge.
    private void ComputeEdgeAndSnapAnchor()
    {
        var wa = ScreenWorkingAreaAt(_anchor);
        double w = ActualWidth, h = ActualHeight;

        if (_vm.IsVertical)
        {
            double distLeft  = _anchor.X - wa.Left;
            double distRight = wa.Right - (_anchor.X + w);
            _edge = distLeft <= distRight ? Edge.Left : Edge.Right;
            _anchor.X = _edge == Edge.Left ? wa.Left : wa.Right - w;
            _anchor.Y = Math.Clamp(_anchor.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        }
        else
        {
            double distTop    = _anchor.Y - wa.Top;
            double distBottom = wa.Bottom - (_anchor.Y + h);
            _edge = distTop <= distBottom ? Edge.Top : Edge.Bottom;
            _anchor.Y = _edge == Edge.Top ? wa.Top : wa.Bottom - h;
            _anchor.X = Math.Clamp(_anchor.X, wa.Left, Math.Max(wa.Left, wa.Right - w));
        }
    }

    // The off-screen resting position: the bar pushed past its edge with only
    // RevealStrip px still poking into the working area as a hover target.
    private Point HiddenPosition()
    {
        var wa = ScreenWorkingAreaAt(_anchor);
        double w = ActualWidth, h = ActualHeight;
        return _edge switch
        {
            Edge.Left  => new Point(wa.Left + RevealStrip - w, _anchor.Y),
            Edge.Right => new Point(wa.Right - RevealStrip,     _anchor.Y),
            Edge.Top   => new Point(_anchor.X, wa.Top + RevealStrip - h),
            _          => new Point(_anchor.X, wa.Bottom - RevealStrip),
        };
    }

    private static System.Drawing.Rectangle ScreenWorkingAreaAt(Point p) =>
        System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point((int)p.X, (int)p.Y))
            .WorkingArea;

    private void Reveal()
    {
        if (!_autoHide || _revealed) return;
        CancelConceal();
        _revealed = true;
        SlideTo(_anchor);
    }

    private void Conceal()
    {
        // Only a docked bar hides, and never while the pointer is on it or the
        // palette is open — the user is mid-interaction.
        if (!_autoHide || !_docked || !_revealed || _searchWindow is not null || IsMouseOver) return;
        _revealed = false;
        SlideTo(HiddenPosition());
    }

    private void ScheduleConceal()
    {
        CancelConceal();
        // Short grace period so a momentary pointer exit doesn't yank the bar away.
        _concealTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _concealTimer.Tick += (_, _) => { CancelConceal(); Conceal(); };
        _concealTimer.Start();
    }

    private void CancelConceal()
    {
        _concealTimer?.Stop();
        _concealTimer = null;
    }

    // Sets position instantly while guarding against persisting the transient move.
    // The guard is cleared on a later dispatcher pass so a deferred LocationChanged
    // is still covered.
    private void SetWindowPosition(Point p)
    {
        _sliding = true;
        Left = p.X;
        Top  = p.Y;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _sliding = false);
    }

    // Ease-out slide between the current position and the target, driven by a timer
    // so we never animate Window.Left/Top directly (whose animation clock would
    // otherwise fight manual assignment afterwards).
    private void SlideTo(Point target)
    {
        StopSlide();
        _sliding = true;
        var start = new Point(Left, Top);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const double durMs = 140;

        _slideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _slideTimer.Tick += (_, _) =>
        {
            double t = Math.Clamp(sw.Elapsed.TotalMilliseconds / durMs, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            Left = start.X + (target.X - start.X) * e;
            Top  = start.Y + (target.Y - start.Y) * e;
            if (t >= 1)
            {
                StopSlide();
                Left = target.X;
                Top  = target.Y;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _sliding = false);
            }
        };
        _slideTimer.Start();
    }

    private void StopSlide()
    {
        _slideTimer?.Stop();
        _slideTimer = null;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_autoHide) { CancelConceal(); Reveal(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_autoHide && _docked && !IsActive && _searchWindow is null) ScheduleConceal();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_autoHide && _docked) Reveal();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_autoHide && _docked && !IsMouseOver && _searchWindow is null) ScheduleConceal();
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
        // next layout pass. Queue the follow-up at Loaded priority so it runs after
        // that pass, otherwise we'd be measuring against the pre-scale size.
        // RefreshAutoHide re-docks when auto-hide is on and falls back to the plain
        // clamp when it's off.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefreshAutoHide);
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
            stored.LastUsed = DateTime.UtcNow;
            return stored;
        }

        // No exact match for this layout — reuse the most-recently-used position
        // that still lands on a connected monitor, so disconnecting/rearranging
        // displays doesn't dump the window back onto the primary at (100, 100).
        // Ordering by LastUsed (descending) makes the choice deterministic across
        // restarts, instead of relying on dictionary enumeration order.
        var bestCandidate = _config.WindowPositions.Values
            .Where(c => DisplayLayout.IsVisibleOn(c.Left, c.Top, ActualWidth, ActualHeight))
            .OrderByDescending(c => c.LastUsed)
            .FirstOrDefault();

        if (bestCandidate is not null)
        {
            var reused = new WindowPosition
            {
                Left = bestCandidate.Left,
                Top  = bestCandidate.Top,
                LastUsed = DateTime.UtcNow,
            };
            _config.WindowPositions[signature] = reused;
            // No Save here: setting Left/Top in the caller fires OnLocationChanged
            // which persists the entry. Saving twice on cold start was wasted work.
            return reused;
        }

        var (dl, dt) = DisplayLayout.DefaultPosition();
        var fresh = new WindowPosition { Left = dl, Top = dt, LastUsed = DateTime.UtcNow };
        _config.WindowPositions[signature] = fresh;
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
        if (vertical)
        {
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

        // Tiles carry a uniform 2px margin assigned at insertion (InsertTile), and
        // it's identical in both wrap directions — so there's nothing per-tile to
        // touch here when orientation flips.
        ApplyCrossAxisSize();
    }

    // Sets only the fixed cross-axis dimension (width in vertical mode, height in
    // horizontal mode) from the current column/row count, letting the main axis
    // auto-size. Split out from ApplyOrientation so the resize-grip drag — which
    // never changes orientation — can re-flow the bar on every mouse-move without
    // rebuilding the panel tree, reassigning host orientations, or walking every
    // tile.
    private void ApplyCrossAxisSize()
    {
        // Clamp for rendering only — never write back. The user's chosen
        // CrossAxisCount stays in the VM intact, so deleting shortcuts down to
        // fewer than that count and re-adding them later restores the original
        // layout instead of leaving it stuck at the smaller value.
        int maxCount = Math.Max(1, _vm.Shortcuts.Count);
        int n = Math.Clamp(_vm.CrossAxisCount, 1, maxCount);

        // OuterPanel's host margins (8 px on the cross axis) scale with the
        // LayoutTransform; the outer Border (1 px each side) does not.
        double scaled = (n * TileStep + 8) * _vm.Scale + 2;

        SizeToContent = SizeToContent.Manual;
        if (_vm.IsVertical)
        {
            Width  = scaled;        // fixed width from column count
            Height = double.NaN;    // height auto-sizes
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            Height = scaled;        // fixed height from row count
            Width  = double.NaN;    // width auto-sizes
            SizeToContent = SizeToContent.Width;
        }
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
        // Orientation is fixed during a drag — only the column/row count changes,
        // so re-flow the cross-axis size instead of rebuilding the whole layout.
        ApplyCrossAxisSize();
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
        if (e.ButtonState != MouseButtonState.Pressed) return;

        DragMove(); // blocks until the mouse button is released

        // With auto-hide on, decide whether the drop landed against an edge
        // (magnetic dock) or out in the open (undock and stay visible).
        if (_autoHide) EvaluateDockAfterDrag();
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
            ApplyHotkey();
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

        // Auto-hide moves the window programmatically; never persist those transient
        // or off-screen coordinates as the user's position.
        if (_sliding) return;
        if (_autoHide && !_revealed) return;
        // A move while revealed and not animating is a genuine user drag — track it
        // as the new docked anchor.
        if (_autoHide) _anchor = new Point(Left, Top);

        PersistPosition(Left, Top);
    }

    private void PersistPosition(double left, double top)
    {
        if (!_config.WindowPositions.TryGetValue(_displaySignature, out var entry))
        {
            entry = new WindowPosition();
            _config.WindowPositions[_displaySignature] = entry;
        }
        entry.Left = left;
        entry.Top  = top;
        entry.LastUsed = DateTime.UtcNow;
        _store.Save(_config);
    }

    internal void PersistShortcuts()
    {
        _vm.ApplyTo(_config);
        _store.Save(_config);
    }

    // Synchronous persist for paths that exit the process immediately after
    // (e.g. self-update restart) — the debounced Save would race the new
    // process loading the config from disk.
    internal void PersistImmediate()
    {
        _vm.ApplyTo(_config);
        _store.SaveImmediate(_config);
    }
}
