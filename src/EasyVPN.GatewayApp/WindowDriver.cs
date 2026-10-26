using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;

namespace EasyVPN.GatewayApp;

/// <summary>
/// What the drivers of VPN clients without a command line (<see cref="TosiClient"/>,
/// <see cref="GlobalProtect"/>) share: looking at another program's window through UI Automation,
/// and pressing, typing and clicking in it as a person would - which needs the session's screen on
/// and unlocked.
/// </summary>
internal static class WindowDriver
{
    /// <summary>One element of a window as it was when looked at.</summary>
    internal sealed record Seen(AutomationElement Element, string Name, ControlType Type, Rect Rect, bool Offscreen, bool Enabled, string Help,
        bool ReadOnly = false, string Value = "", bool Password = false)
    {
        public bool Visible => !Offscreen && !Rect.IsEmpty;
    }

    /// <summary>
    /// Every element of the window, with what is needed of each fetched in one go: quick even when a
    /// client lists a hundred rows, and steady while it changes underneath.
    /// </summary>
    internal static List<Seen> Look(AutomationElement window)
    {
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty, AutomationElement.HelpTextProperty,
                     AutomationElement.IsPasswordProperty, ValuePattern.IsReadOnlyProperty, ValuePattern.ValueProperty })
            cache.Add(property);
        AutomationElementCollection all;
        using (cache.Activate())
            all = window.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        // Out of the window is out of view, whatever an element says of itself: a list scrolled past
        // a row may still call it on screen.
        var bounds = window.Current.BoundingRectangle;
        var seen = new List<Seen>(all.Count);
        foreach (AutomationElement e in all)
        {
            try
            {
                var c = e.Cached;
                var rect = c.BoundingRectangle;
                var inside = !rect.IsEmpty && bounds.Contains(new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
                // A box's value and whether it takes typing, where it says.
                var readOnly = e.GetCachedPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true;
                var value = e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string ?? "";
                seen.Add(new Seen(e, c.Name ?? "", c.ControlType, rect, c.IsOffscreen || !inside, c.IsEnabled, c.HelpText ?? "", readOnly, value, c.IsPassword));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // Gone already.
            }
        }
        return seen;
    }

    /// <summary>A button: pressed through UI Automation where it can be, clicked where it cannot.</summary>
    internal static void Press(IntPtr window, AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        var r = button.Current.BoundingRectangle;
        Click(window, new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
    }

    /// <summary>
    /// Puts text in a box as a person does - a click into it, the text typed - as a client may react
    /// to typing alone (an OK button, a search). Typed only once its window is in front, so nothing
    /// lands anywhere else, and read back where it can be. Otherwise put there through UI Automation.
    /// </summary>
    /// <returns>True when typed, false when put there otherwise, null when neither would do.</returns>
    internal static bool? Enter(IntPtr window, AutomationElement box, string text)
    {
        try
        {
            Front(window);
            // A click focuses the box as surely as a person's, and brings the window to the front
            // with it; UI Automation's focus as well, should the click not get there.
            var r = box.Current.BoundingRectangle;
            if (!r.IsEmpty && !box.Current.IsOffscreen) TryClick(window, new Point(r.Left + Math.Min(r.Width - 4, Math.Max(8, r.Width / 2)), r.Top + r.Height / 2), hover: false);
            if (GetForegroundWindow() != window) Front(window);
            box.SetFocus();
            if (GetForegroundWindow() == window)
            {
                SelectAll();
                PressKey(0x08); // Backspace
                TypeText(text);
                if (ValueOf(box) is not { } now || now == text) return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            // The desktop takes no typing (locked, or a disconnected Remote Desktop): the other way.
        }
        try
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
            {
                ((ValuePattern)value).SetValue(text);
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotEnabledException or COMException)
        {
            // Nor this.
        }
        return null;
    }

    /// <summary>What a box holds, when it can be read: not a password.</summary>
    internal static string? ValueOf(AutomationElement box)
    {
        try
        {
            return !box.Current.IsPassword && box.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return null;
        }
    }

    internal static bool WaitUntil(Func<bool> done, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            try
            {
                if (done()) return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
            {
                // Changed while it was looked at: look again.
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
    }

    /// <summary>An element and everything in it, a line each, for working out why a client could not be driven.</summary>
    internal static void Write(AutomationElement element, int depth, Action<string> say)
    {
        var c = element.Current;
        var r = c.BoundingRectangle;
        var patterns = string.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")));
        say($"{new string(' ', depth * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{c.Name}\" id={c.AutomationId} class={c.ClassName} " +
            $"rect={(r.IsEmpty ? "-" : $"{r.Left:0},{r.Top:0},{r.Width:0}x{r.Height:0}")}{(c.IsEnabled ? "" : " disabled")}{(c.IsOffscreen ? " offscreen" : "")}{(c.IsPassword ? " password" : "")} [{patterns}]");
        if (depth > 25) return;
        for (var child = TreeWalker.RawViewWalker.GetFirstChild(element); child is not null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
            Write(child, depth + 1, say);
    }

    // ---------------------------------------------------------------- Windows

    /// <summary>A top-level window by its title, shown or not: a client may sit hidden in the notification area.</summary>
    internal static IntPtr FindWindow(Regex title)
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var text = new StringBuilder(256);
            GetWindowText(handle, text, text.Capacity);
            if (!title.IsMatch(text.ToString())) return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>A window's title, for saying which window.</summary>
    internal static string Title(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetWindowText(handle, text, text.Capacity);
        return text.Length > 0 ? text.ToString() : "the client's window";
    }

    /// <summary>
    /// Shown, restored and in front, so the client lays out its window and takes typing. Windows lets
    /// only the program in front give the front away, so this borrows its input for a moment.
    /// </summary>
    /// <returns>True when the window is in front.</returns>
    internal static bool Front(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
        var foreground = GetForegroundWindow();
        if (foreground != handle)
        {
            var theirs = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var ours = GetCurrentThreadId();
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached) AttachThreadInput(ours, theirs, false);
            }
            Thread.Sleep(300);
        }
        return GetForegroundWindow() == handle;
    }

    /// <summary>
    /// A click as a person makes it: the pointer moved there, a moment for it to be noticed - a
    /// client that draws its own buttons may take a click only on one it has seen the pointer over -
    /// then a press and, a moment later, a release.
    /// </summary>
    /// <param name="hover">The pointer kept there a moment first. A box takes a click without it.</param>
    /// <exception cref="InvalidOperationException">Another window is over the window there: no click, as it would land on that.</exception>
    internal static void Click(IntPtr window, Point at, bool hover = true)
    {
        // Only ever on the client: brought to the front just before, and the click made only where
        // Windows says it is the window there - otherwise it lands on whatever is in front.
        Front(window);
        if (!Over(window, at))
        {
            Thread.Sleep(300);
            Front(window);
            if (!Over(window, at))
                throw new InvalidOperationException($"Another window is over {Title(window)} where it was to be clicked ({at.X:0},{at.Y:0}), so nothing was clicked.");
        }
        SetCursorPos((int)at.X - 3, (int)at.Y);
        Thread.Sleep(hover ? 50 : 20);
        Send(Move(1, 0), Move(1, 0), Move(1, 0)); // arrives, as a hand's would
        Thread.Sleep(hover ? 350 : 30);
        Send(Mouse(0x0002)); // left down
        Thread.Sleep(hover ? 90 : 40);
        Send(Mouse(0x0004)); // left up
        Thread.Sleep(hover ? 250 : 60);
    }

    /// <summary>Whether the window at a point on the screen is (part of) <paramref name="window"/>.</summary>
    private static bool Over(IntPtr window, Point at) => GetAncestor(WindowFromPoint(new NativePoint { X = (int)at.X, Y = (int)at.Y }), 2 /* GA_ROOT */) == window;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    private static Input Move(int dx, int dy) => new() { Type = 0, Union = new InputUnion { Mouse = new MouseInput { X = dx, Y = dy, Flags = 0x0001 } } };

    internal static void TryClick(IntPtr window, Point at, bool hover = true)
    {
        try
        {
            Click(window, at, hover);
        }
        catch (InvalidOperationException)
        {
            // No mouse to be had here; the other ways will do.
        }
    }

    private static void SelectAll() => Send(Key(0x11, 0), Key(0x41, 0), Key(0x41, 2), Key(0x11, 2)); // Ctrl+A

    internal static void PressKey(ushort key) => Send(Key(key, 0), Key(key, 2));

    /// <summary>Types text as it is, character by character.</summary>
    internal static void TypeText(string text)
    {
        if (text.Length > 0) Send([.. text.SelectMany(c => new[] { Unicode(c, 0), Unicode(c, 2) })]);
        Thread.Sleep(200);
    }

    private static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows did not take the keyboard or mouse input: the gateway's desktop must be signed in and unlocked for this.");
    }

    private static Input Mouse(uint flags) => new() { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
    private static Input Key(ushort key, uint flags) => new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = flags } } };
    private static Input Unicode(char c, uint flags) => new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = flags | 0x0004 } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }

    internal delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, IntPtr processId);

    /// <summary>The thread that made a window, and the process it is in.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
