using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Toolbar;

public partial class App : Application
{
    private const string MutexName = "Toolbar_SingleInstance_7F3A2B1C";

    private Mutex? _mutex;
    private NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Toolbar — Unhandled Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);

        if (!isNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

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
            icon = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? AppContext.BaseDirectory)
                ?? System.Drawing.SystemIcons.Application;
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

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* already released or abandoned */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
