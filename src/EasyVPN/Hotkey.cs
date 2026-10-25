using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EasyVPN;

/// <summary>
/// A shortcut that opens the tray menu from anywhere, as DynatecRDM's Ctrl+Alt+R does. It hangs
/// off a hidden zero-sized window, so it works while every real window is in the tray. Taken
/// by another program, or written wrong in app.json, there is simply none.
/// </summary>
public sealed class Hotkey : IDisposable
{
    private const int Id = 0x4556; // 'EV'
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource? _source;
    private readonly Action _pressed;

    private Hotkey(HwndSource source, Action pressed)
    {
        _source = source;
        _pressed = pressed;
        source.AddHook(WndProc);
    }

    /// <summary>Registers <paramref name="gesture"/> (such as <c>Ctrl+Alt+F</c>), or returns null when it cannot.</summary>
    public static Hotkey? Register(string? gesture, Action pressed)
    {
        if (!TryParse(gesture, out var modifiers, out var key)) return null;

        // Not a message-only window: WM_HOTKEY is never sent to those. A zero-sized tool window
        // is never seen, nor in the taskbar or Alt+Tab, and does get it.
        var source = new HwndSource(new HwndSourceParameters("EasyVPN.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW
        });
        if (RegisterHotKey(source.Handle, Id, modifiers | ModNoRepeat, key)) return new Hotkey(source, pressed);
        source.Dispose();
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam != Id) return IntPtr.Zero;
        handled = true;
        // After the hook returns: a window is never opened inside a window procedure.
        _source?.Dispatcher.BeginInvoke(_pressed);
        return IntPtr.Zero;
    }

    private static bool TryParse(string? gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in (gesture ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => 0u,
            };
            if (modifier != 0) modifiers |= modifier;
            else if (key != 0) return false;
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
            else if (part.Length is 2 or 3 && part[0] is 'F' or 'f' && int.TryParse(part.AsSpan(1), out var f) && f is >= 1 and <= 24) key = (uint)(0x6F + f);
            else return false;
        }
        return modifiers != 0 && key != 0;
    }

    public void Dispose()
    {
        if (_source is null) return;
        UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
