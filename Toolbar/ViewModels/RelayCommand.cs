using System.Windows.Input;

namespace Toolbar.ViewModels;

public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => canExecute?.Invoke() ?? true;
    public void Execute(object? _) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
