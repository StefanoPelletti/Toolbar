using System.Windows;
using System.Windows.Input;
using Toolbar.Services;
using Toolbar.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Toolbar;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        AlwaysOnTopBox.IsChecked = vm.AlwaysOnTop;
        LaunchAtBootBox.IsChecked = vm.LaunchAtBoot;
        VerticalBox.IsChecked = vm.IsVertical;
        ScaleSlider.Value = vm.ScaleSteps;
        UpdateScaleLabel(vm.ScaleSteps);
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateScaleLabel((int)e.NewValue);

    private void UpdateScaleLabel(int steps)
        => ScaleLabel.Text = $"{100 + steps * 10}%";

    private void OnTitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _vm.IsVertical = VerticalBox.IsChecked == true;
        _vm.ScaleSteps = (int)ScaleSlider.Value;

        bool bootEnabled = LaunchAtBootBox.IsChecked == true;
        _vm.LaunchAtBoot = bootEnabled;
        AutoStartService.Apply(bootEnabled);

        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        base.OnKeyDown(e);
    }
}
