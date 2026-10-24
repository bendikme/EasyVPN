using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;

namespace EasyVPN.GatewayApp;

/// <summary>
/// Tosi Client (once TOSIBOX Key) has no command line, so the gateway's tosibox connector has this
/// app, started in the desktop session where the client runs, do what a person would there:
/// enter the client's password if it asks, search for the Lock, press its Connect or Disconnect,
/// and wait until the client shows it so. Through UI Automation where the client offers it, and
/// with the mouse and keyboard where it does not - which needs the session's screen unlocked.
///
///   EasyVPN.GatewayApp.exe --tosibox connect "Tosibox 15 [Plant]" [--start]   the password in EASYVPN_PASSWORD
///   EasyVPN.GatewayApp.exe --tosibox disconnect "Tosibox 15 [Plant]"
///   EasyVPN.GatewayApp.exe --tosibox dump > tosi.txt                     what it sees of the client
///
/// With --start, a client that shows no window here is started: from EASYVPN_PROGRAM, or as the
/// installed programs list it. Started again, a running one that sits in the notification area
/// shows itself.
/// </summary>
public static partial class TosiClient
{
    /// <summary>The exit code for "no Tosi Client on this desktop": the gateway tries the next session.</summary>
    public const int NotHere = 3;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Connecting = TimeSpan.FromSeconds(75);

    public static int Run(string[] args, Action<string> say)
    {
        var password = Environment.GetEnvironmentVariable("EASYVPN_PASSWORD");
        try
        {
            return args switch
            {
                ["connect", var name, .. var options] => Switch(name.Trim(), connect: true, password, options.Contains("--start"), say),
                ["disconnect", var name] => Switch(name.Trim(), connect: false, password, start: false, say),
                ["dump"] => Dump(say),
                _ => Usage(say),
            };
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            say($"Tosi Client could not be driven: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // Anything else is a mistake here: said, and written down whole, but never a crash.
            CrashLog.Write(ex, "driving Tosi Client");
            say($"Tosi Client could not be driven ({ex.GetType().Name}: {ex.Message}). The details are in {CrashLog.Path} on the gateway.");
            return 1;
        }
    }

    private static int Usage(Action<string> say)
    {
        say("--tosibox connect <Lock> [--start] | disconnect <Lock> | dump");
        return 1;
    }

    // ---------------------------------------------------------------- connect, disconnect

    private static int Switch(string name, bool connect, string? password, bool start, Action<string> say)
    {
        var showing = FindWindow(MainTitle()) != IntPtr.Zero || FindWindow(PasswordTitle()) != IntPtr.Zero;
        if (!showing && start && !Start(say)) return 1;
        if (!SignIn(password, say, out var code)) return code;

        var handle = FindWindow(MainTitle());
        if (handle == IntPtr.Zero)
        {
            say("Tosi Client is not running on this desktop.");
            return NotHere;
        }
        Bring(handle);
        var window = AutomationElement.FromHandle(handle);
        var search = SearchBox(window);
        try
        {
            var row = Find(window, search, name);
            if (row is null)
            {
                say($"Tosi Client lists no Lock called '{name}'.");
                return 1;
            }

            var state = Read(window, row);
            if (state.Connected == connect)
            {
                say(connect ? $"{name} is connected in Tosi Client." : $"{name} is not connected in Tosi Client.");
                return 0;
            }
            if (connect && state.Button is { Current.IsEnabled: false })
            {
                say($"{name} cannot be connected: {state.Status}");
                return 1;
            }

            Press(window, row, state);
            say($"Pressed {(connect ? "Connect" : "Disconnect")} for {name} in Tosi Client.");

            // Done when the client says so: its button turns, its status follows.
            var deadline = DateTime.UtcNow + (connect ? Connecting : Settle);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                row = Find(window, search, name, research: false) ?? row;
                state = Read(window, row);
                if (state.Connected == connect)
                {
                    say(connect ? $"Tosi Client has connected {name}." : $"Tosi Client has disconnected {name}.");
                    return 0;
                }
            }
            say($"Tosi Client did not show {name} {(connect ? "connected" : "disconnected")} within {(connect ? Connecting : Settle).TotalSeconds:0} s. It says: {state.Status}");
            return 1;
        }
        finally
        {
            // Leave the list as a person left it.
            if (search is not null) TryEnter(search, "");
        }
    }

    /// <summary>Starts the client, or brings a running one out of the notification area, and waits for its window.</summary>
    private static bool Start(Action<string> say)
    {
        var program = Program();
        if (program is null)
        {
            say("Tosi Client is not running, and is not among the installed programs: give the facility its program (the client's .exe).");
            return false;
        }
        say($"Starting Tosi Client ({program}).");
        Process.Start(new ProcessStartInfo(program) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program) })?.Dispose();
        if (!WaitUntil(() => FindWindow(MainTitle()) != IntPtr.Zero || FindWindow(PasswordTitle()) != IntPtr.Zero, TimeSpan.FromSeconds(45)))
        {
            say("Tosi Client was started, but showed no window within 45 s.");
            return false;
        }
        Thread.Sleep(1500); // Its list fills in a moment later.
        return true;
    }

    /// <summary>The client's program: the one set, or the installed one, as Windows' list of programs has it.</summary>
    private static string? Program()
    {
        if (Environment.GetEnvironmentVariable("EASYVPN_PROGRAM") is { Length: > 0 } set) return File.Exists(set) ? set : null;

        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var programs = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var key in programs?.GetSubKeyNames() ?? [])
            {
                using var entry = programs!.OpenSubKey(key);
                if (entry?.GetValue("DisplayName") is not string name || !Product().IsMatch(name)) continue;
                if (entry.GetValue("DisplayIcon") is string icon && Client(icon.Split(',')[0].Trim().Trim('"')) is { } fromIcon) return fromIcon;
                if (entry.GetValue("InstallLocation") is not string folder || !Directory.Exists(folder)) continue;
                // Its folder may hold other programs too (OpenVPN, for one): the one named for the client, or the only one.
                var found = Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
                    .Select(Client).OfType<string>().ToList();
                if ((found.FirstOrDefault(p => ClientName().IsMatch(Path.GetFileNameWithoutExtension(p))) ?? (found.Count == 1 ? found[0] : null)) is { } inFolder)
                    return inFolder;
            }
        }
        return null;
    }

    /// <summary>The path, when it is the client itself rather than its uninstaller, updater or service.</summary>
    private static string? Client(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !NotTheClient().IsMatch(Path.GetFileName(path)) ? path : null;

    /// <summary>The client's password, when it asks for it - when it has just started.</summary>
    private static bool SignIn(string? password, Action<string> say, out int code)
    {
        code = 0;
        var handle = FindWindow(PasswordTitle());
        if (handle == IntPtr.Zero) return true;
        if (string.IsNullOrEmpty(password))
        {
            say("Tosi Client asks for its password: give the facility the client's password in the facility editor.");
            code = 1;
            return false;
        }

        Bring(handle);
        var dialog = AutomationElement.FromHandle(handle);
        var box = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (box is null || !TryEnter(box, password))
        {
            say("Tosi Client's password window has no box that can be typed in.");
            code = 1;
            return false;
        }
        var ok = Buttons(dialog).FirstOrDefault(b => Is(b, "OK"));
        if (ok is not null) Press(ok);
        else PressKey(0x0D); // Enter

        if (!WaitUntil(() => FindWindow(PasswordTitle()) == IntPtr.Zero, Settle))
        {
            say("Tosi Client did not take the password: check the facility's password.");
            code = 1;
            return false;
        }
        WaitUntil(() => FindWindow(MainTitle()) != IntPtr.Zero, Settle);
        return true;
    }

    /// <summary>
    /// The Lock's row: searched for by its whole name, then by the part in brackets and the part
    /// before them, then in the whole list, as the client's search may match only some of it.
    /// </summary>
    private static AutomationElement? Find(AutomationElement window, AutomationElement? search, string name, bool research = true)
    {
        if (search is null || !research) return Row(window, name);
        var brackets = Regex.Match(name, @"^(.*?)\s*\[(.+)\]\s*$");
        string[] terms = brackets.Success ? [name, brackets.Groups[2].Value, brackets.Groups[1].Value, ""] : [name, ""];
        foreach (var term in terms.Distinct())
        {
            if (!TryEnter(search, term)) return Row(window, name);
            AutomationElement? row = null;
            if (WaitUntil(() => (row = Row(window, name)) is not null, TimeSpan.FromSeconds(3))) return row;
        }
        return null;
    }

    private static AutomationElement? Row(AutomationElement window, string name)
    {
        var named = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase));
        return named.Cast<AutomationElement>().FirstOrDefault(e =>
            e.Current.ControlType != ControlType.Edit && !e.Current.IsOffscreen && !e.Current.BoundingRectangle.IsEmpty);
    }

    private sealed record State(bool Connected, AutomationElement? Button, string Status);

    /// <summary>What the client shows beside the row: its button, if it is one UI Automation sees, and its status.</summary>
    private static State Read(AutomationElement window, AutomationElement row)
    {
        var line = row.Current.BoundingRectangle;
        bool Beside(AutomationElement e)
        {
            var r = e.Current.BoundingRectangle;
            return !r.IsEmpty && r.Top + r.Height / 2 >= line.Top && r.Top + r.Height / 2 <= line.Bottom;
        }

        var button = Buttons(window).Where(Beside).FirstOrDefault(b => ConnectButton().IsMatch(b.Current.Name));
        var status = string.Join(" ", window.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
            .Where(e => e.Current.ControlType != ControlType.Button && e.Current.ControlType != ControlType.Edit && Beside(e))
            .Select(e => e.Current.Name.Trim())
            // Not the Lock's own name, nor a button's label inside the button.
            .Where(text => text.Length > 0 && !string.Equals(text, row.Current.Name, StringComparison.OrdinalIgnoreCase) && !ConnectButton().IsMatch(text))
            .Distinct());

        var connected = button is not null
            ? button.Current.Name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase)
            : Connected().IsMatch(status) && !NotConnected().IsMatch(status);
        return new State(connected, button, status);
    }

    /// <summary>The row's button, or where the Connection column crosses the row when the client draws its buttons itself.</summary>
    private static void Press(AutomationElement window, AutomationElement row, State state)
    {
        if (state.Button is { } button)
        {
            Press(button);
            return;
        }
        var column = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Connection", PropertyConditionFlags.IgnoreCase))
            ?? throw new InvalidOperationException("Tosi Client shows no Connect button, and no Connection column to find one in. Run --tosibox dump and look.");
        var x = column.Current.BoundingRectangle;
        var y = row.Current.BoundingRectangle;
        Click(new Point(x.Left + x.Width / 2, y.Top + y.Height / 2));
    }

    private static void Press(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        var r = button.Current.BoundingRectangle;
        Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
    }

    private static AutomationElement? SearchBox(AutomationElement window)
    {
        var boxes = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
            .Cast<AutomationElement>().ToList();
        return boxes.FirstOrDefault(b => (b.Current.Name + b.Current.HelpText).Contains("Search", StringComparison.OrdinalIgnoreCase))
            ?? boxes.FirstOrDefault();
    }

    private static IEnumerable<AutomationElement> Buttons(AutomationElement within) =>
        within.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>();

    private static bool Is(AutomationElement e, string name) => string.Equals(e.Current.Name.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts text in a box: through UI Automation, or by typing it into the focused box.</summary>
    private static bool TryEnter(AutomationElement box, string text)
    {
        try
        {
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
            {
                ((ValuePattern)value).SetValue(text);
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotEnabledException or COMException)
        {
            // Password boxes often refuse it; typing does not.
        }
        try
        {
            box.SetFocus();
            SelectAll();
            PressKey(0x08); // Backspace
            Type(text);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> done, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!done())
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
        return true;
    }

    // ---------------------------------------------------------------- what it sees

    private static int Dump(Action<string> say)
    {
        var found = false;
        foreach (var title in new[] { MainTitle(), PasswordTitle() })
        {
            var handle = FindWindow(title);
            if (handle == IntPtr.Zero) continue;
            found = true;
            Write(AutomationElement.FromHandle(handle), 0, say);
        }
        if (!found) say("No Tosi Client window on this desktop.");
        return found ? 0 : NotHere;
    }

    private static void Write(AutomationElement element, int depth, Action<string> say)
    {
        var c = element.Current;
        var r = c.BoundingRectangle;
        var patterns = string.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")));
        say($"{new string(' ', depth * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{c.Name}\" id={c.AutomationId} class={c.ClassName} " +
            $"rect={(r.IsEmpty ? "-" : $"{r.Left:0},{r.Top:0},{r.Width:0}x{r.Height:0}")}{(c.IsEnabled ? "" : " disabled")}{(c.IsOffscreen ? " offscreen" : "")} [{patterns}]");
        if (depth > 25) return;
        for (var child = TreeWalker.RawViewWalker.GetFirstChild(element); child is not null; child = TreeWalker.RawViewWalker.GetNextSibling(child))
            Write(child, depth + 1, say);
    }

    // ---------------------------------------------------------------- Windows

    [GeneratedRegex(@"^(Tosi|TOSIBOX) Key\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainTitle();

    [GeneratedRegex(@"^(Tosi|TOSIBOX)\b.*password", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordTitle();

    [GeneratedRegex(@"^(Dis)?connect$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectButton();

    [GeneratedRegex(@"\b(Tosi|TOSIBOX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Product();

    [GeneratedRegex(@"unins|uninstall|update|service|helper|crash|driver|tapctl|devcon|setup|openvpn|redist", RegexOptions.IgnoreCase)]
    private static partial Regex NotTheClient();

    [GeneratedRegex(@"tosi|key|client", RegexOptions.IgnoreCase)]
    private static partial Regex ClientName();

    [GeneratedRegex(@"\bconnected\b", RegexOptions.IgnoreCase)]
    private static partial Regex Connected();

    [GeneratedRegex(@"\b(not|dis)\s*connected\b|ready to connect", RegexOptions.IgnoreCase)]
    private static partial Regex NotConnected();

    /// <summary>A top-level window by its title, shown or not: the client may sit hidden in the notification area.</summary>
    private static IntPtr FindWindow(Regex title)
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

    /// <summary>Shown, restored and in front, so the client lays out its list and takes input.</summary>
    private static void Bring(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
        SetForegroundWindow(handle);
        Thread.Sleep(300);
    }

    private static void Click(Point at)
    {
        SetCursorPos((int)at.X, (int)at.Y);
        Send(Mouse(0x0002), Mouse(0x0004)); // left down, left up
        Thread.Sleep(200);
    }

    private static void SelectAll() => Send(Key(0x11, 0), Key(0x41, 0), Key(0x41, 2), Key(0x11, 2)); // Ctrl+A

    private static void PressKey(ushort key) => Send(Key(key, 0), Key(key, 2));

    /// <summary>Types text as it is, character by character.</summary>
    private static void Type(string text)
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

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
