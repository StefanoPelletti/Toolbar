using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Toolbar.Services;

/// <summary>
/// Registers a single system-wide hotkey against a window handle and raises
/// <see cref="Pressed"/> when it fires. Re-registering swaps the binding, so the
/// gesture can be changed at runtime from Settings.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;

    [Flags]
    private enum Mods : uint { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000 }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>Raised on the UI thread when the registered hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>Hook the message loop. Call once, after the window handle exists.</summary>
    public void Attach(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Registers <paramref name="gesture"/> (e.g. "Ctrl+Alt+Space"), replacing any
    /// prior binding. Returns false if the gesture is unparseable or the OS refuses
    /// it (already taken by another app), leaving no binding active.
    /// </summary>
    public bool Register(string? gesture)
    {
        Unregister();
        if (_source is null) return false;
        if (!TryParse(gesture, out var mods, out var key)) return false;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        _registered = RegisterHotKey(_source.Handle, HotkeyId, (uint)(mods | Mods.NoRepeat), vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    /// <summary>Validate a gesture string without registering it (for the Settings UI).</summary>
    public static bool IsValid(string? gesture) => TryParse(gesture, out _, out _);

    // A usable gesture needs at least one modifier plus a non-modifier key — bare
    // keys can't be global hotkeys and would also steal normal typing.
    private static bool TryParse(string? gesture, out Mods mods, out Key key)
    {
        mods = Mods.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (var raw in gesture.Split('+',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Mods.Control; break;
                case "alt":                  mods |= Mods.Alt;     break;
                case "shift":                mods |= Mods.Shift;   break;
                case "win": case "windows":  mods |= Mods.Win;     break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && mods != Mods.None;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }
}
