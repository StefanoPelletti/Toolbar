using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbar.Services;
using Toolbar.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Toolbar.Controls;

/// <summary>
/// Keyboard-driven quick-launch palette. A real top-level window (rather than a
/// Popup) so it reliably takes keyboard focus when activated: type to filter,
/// Up/Down to move the selection, Enter or double-click to launch, Esc or losing
/// focus to dismiss.
/// </summary>
public partial class SearchWindow : Window
{
    private readonly List<ShortcutViewModel> _all;
    private readonly ObservableCollection<ShortcutViewModel> _results = [];

    public SearchWindow(IEnumerable<ShortcutViewModel> shortcuts)
    {
        InitializeComponent();
        _all = shortcuts.ToList();
        SearchList.ItemsSource = _results;
        Populate(string.Empty);

        Loaded += (_, _) =>
        {
            ClampOnScreen();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
        // Click away / Alt-Tab elsewhere dismisses the palette.
        Deactivated += (_, _) => Close();
    }

    /// <summary>Place the palette just below the bar, then it self-clamps once sized.</summary>
    public void PositionNear(Window owner)
    {
        Left = owner.Left;
        Top  = owner.Top + owner.ActualHeight + 4;
    }

    private void ClampOnScreen()
    {
        var wa = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point((int)Left, (int)Top))
            .WorkingArea;

        if (ActualWidth <= wa.Width)
            Left = Math.Clamp(Left, wa.Left, wa.Right - ActualWidth);
        if (ActualHeight <= wa.Height)
            Top = Math.Clamp(Top, wa.Top, wa.Bottom - ActualHeight);
    }

    private void Populate(string query)
    {
        _results.Clear();
        IEnumerable<ShortcutViewModel> matches = string.IsNullOrWhiteSpace(query)
            ? _all
            : _all.Where(s => s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var m in matches) _results.Add(m);
        if (_results.Count > 0) SearchList.SelectedIndex = 0;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Populate(SearchBox.Text);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:   Move(+1);        e.Handled = true; break;
            case Key.Up:     Move(-1);        e.Handled = true; break;
            case Key.Enter:  LaunchSelected(); e.Handled = true; break;
            case Key.Escape: Close();          e.Handled = true; break;
            default: base.OnPreviewKeyDown(e); break;
        }
    }

    private void Move(int delta)
    {
        if (_results.Count == 0) return;
        int idx = Math.Clamp(SearchList.SelectedIndex + delta, 0, _results.Count - 1);
        SearchList.SelectedIndex = idx;
        SearchList.ScrollIntoView(SearchList.SelectedItem);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelected();

    private void LaunchSelected()
    {
        if (SearchList.SelectedItem is not ShortcutViewModel vm) return;
        // Close the palette first so the broken-shortcut dialog (if any) is
        // parented by MainWindow rather than the now-dismissed palette.
        var owner = Owner;
        Close();
        ShortcutLauncher.TryLaunch(vm, owner);
    }
}
