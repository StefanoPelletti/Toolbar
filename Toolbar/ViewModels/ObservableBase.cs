using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Toolbar.ViewModels;

public class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
