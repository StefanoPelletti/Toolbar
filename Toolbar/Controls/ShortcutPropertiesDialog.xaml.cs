using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Toolbar.Controls;

public partial class ShortcutPropertiesDialog : Window
{
    public string? Arguments { get; private set; }
    public bool RunAsAdmin { get; private set; }

    public ShortcutPropertiesDialog(string? arguments, bool runAsAdmin)
    {
        InitializeComponent();
        ArgsBox.Text = arguments ?? string.Empty;
        AdminBox.IsChecked = runAsAdmin;
        Loaded += (_, _) => { ArgsBox.SelectAll(); ArgsBox.Focus(); };
    }

    private void OnTitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var args = ArgsBox.Text.Trim();
        Arguments = string.IsNullOrEmpty(args) ? null : args;
        RunAsAdmin = AdminBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
