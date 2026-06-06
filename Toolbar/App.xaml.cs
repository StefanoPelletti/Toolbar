using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Toolbar.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Toolbar;

public partial class App : Application
{
    private const string MutexName = "Toolbar_SingleInstance_7F3A2B1C";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconHandle; // owned; NotifyIcon does not dispose it (B3)

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Toolbar — Unhandled Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        // The in-app updater relaunches us with --updated. At that moment the
        // outgoing process may still be holding the single-instance mutex for a
        // few hundred ms, so a zero-timeout acquire would mistake the tail of our
        // own shutdown for a second instance and exit — leaving nothing running
        // after an update. Wait briefly in that case; a normal second launch
        // still bails immediately.
        bool afterUpdate = e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase);

        _mutex = new Mutex(initiallyOwned: false, MutexName);
        _ownsMutex = TryAcquire(TimeSpan.Zero)
                     || (afterUpdate && TryAcquire(TimeSpan.FromSeconds(5)));

        if (!_ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        UpdateService.CleanupLeftover();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        System.Drawing.Icon icon;
        try
        {
            _trayIconHandle = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? AppContext.BaseDirectory);
            icon = _trayIconHandle ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (_, _) => ShowMainWindow());
        contextMenu.Items.Add("Settings", null, (_, _) => OpenSettings());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Toolbar",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not Window w) return;
        w.Show();
        w.Activate();
    }

    private void OpenSettings()
    {
        ShowMainWindow();
        if (MainWindow is MainWindow mw)
            mw.OpenSettings();
    }

    private void ExitApp() => Shutdown(); // OnExit handles cleanup

    // Acquire the single-instance mutex, treating an abandoned mutex (previous
    // instance crashed without releasing) as a successful acquire.
    private bool TryAcquire(TimeSpan timeout)
    {
        try { return _mutex!.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIconHandle?.Dispose(); // B3: dispose the owned icon handle separately
        if (_ownsMutex)
            try { _mutex?.ReleaseMutex(); } catch { /* already released or abandoned */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
