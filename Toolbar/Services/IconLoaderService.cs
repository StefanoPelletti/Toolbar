using System.Collections.Concurrent;

namespace Toolbar.Services;

// Dedicated STA thread for shell/COM icon extraction. IShellItemImageFactory,
// IShellLink, and SHGetFileInfo must run on an STA thread; keeping them off
// the UI thread means a cold start with many shortcuts never causes visible
// jank — each extraction runs in the background and the result is marshaled
// back when ready.
internal sealed class IconLoaderService : IDisposable
{
    // Unbounded: Queue() is called from the UI thread (one item per shortcut at
    // startup), and a bounded collection's Add would block the UI thread the
    // moment more shortcuts exist than the bound. Depth is naturally limited by
    // the number of shortcuts on the bar.
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    internal IconLoaderService()
    {
        _thread = new Thread(Run)
        {
            Name = "Toolbar-IconLoader",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    internal void Queue(Action work)
    {
        if (_disposed) return;
        try { _queue.Add(work); }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}
