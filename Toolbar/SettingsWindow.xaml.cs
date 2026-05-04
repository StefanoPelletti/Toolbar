using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Toolbar.ViewModels;

namespace Toolbar;

public partial class SettingsWindow : Window
{
    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Toolbar";

    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        AlwaysOnTopBox.IsChecked = vm.AlwaysOnTop;
        LaunchAtBootBox.IsChecked = vm.LaunchAtBoot;
        VerticalBox.IsChecked = vm.IsVertical;
    }

    private void OnTitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _vm.IsVertical = VerticalBox.IsChecked == true;

        bool bootEnabled = LaunchAtBootBox.IsChecked == true;
        _vm.LaunchAtBoot = bootEnabled;
        ApplyBootSetting(bootEnabled);

        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void ApplyBootSetting(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key is null) return;

            if (enable)
            {
                var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                key.SetValue(AppName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
