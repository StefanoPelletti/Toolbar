using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Toolbar.Controls;

public partial class RenameDialog : Window
{
    public string? Result { get; private set; }

    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        Loaded += (_, _) => { NameBox.SelectAll(); NameBox.Focus(); };
    }

    private void OnTitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
