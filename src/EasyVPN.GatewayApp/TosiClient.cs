using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;
using static EasyVPN.GatewayApp.WindowDriver;

namespace EasyVPN.GatewayApp;

/// <summary>
/// Tosi Client (once TOSIBOX Key) has no command line, so the gateway's tosibox connector has this
/// app, started in the desktop session where the client runs, do what a person would there:
/// enter the client's password if it asks, find the Lock, press its Connect or Disconnect, and
/// wait until the client shows it so. Through UI Automation where the client offers it, and with
/// the mouse and keyboard where it does not - which needs the session's screen unlocked.
///
///   EasyVPN.GatewayApp.exe --tosibox connect "Tosibox 15 [Plant]" [--start]   the password in EASYVPN_PASSWORD
///   EasyVPN.GatewayApp.exe --tosibox disconnect "Tosibox 15 [Plant]"
///   EasyVPN.GatewayApp.exe --tosibox dump > tosi.txt                     what it sees of the client
///
/// With --start, a client that shows no window here is started: from EASYVPN_PROGRAM, or from
/// wherever Windows keeps track of it. Started again, a running one that sits in the notification
/// area shows itself. When the Lock cannot be found or read, what was seen of the client is saved
/// to <see cref="DumpPath"/>, and said.
/// </summary>
public static partial class TosiClient
{
    /// <summary>The exit code for "no Tosi Client on this desktop": the gateway tries the next session.</summary>
    public const int NotHere = 3;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    // A Lock can take a while; the gateway gives the whole connector longer than this.
    private static readonly TimeSpan Connecting = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ListFills = TimeSpan.FromSeconds(45);
    // How often a long wait says it is still waiting, so whoever asked sees something happen.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public static string DumpPath { get; } = Path.Combine(Path.GetDirectoryName(CrashLog.Path)!, "tosi-client.txt");

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
        catch (Exception ex)
        {
            // Never a crash: said, and written down whole.
            CrashLog.Write(ex, "driving Tosi Client");
            say($"Tosi Client could not be driven ({ex.GetType().Name}: {ex.Message}).{Saved()} The details are in {CrashLog.Path} on the gateway.");
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
        // Running here with no window - in the notification area - it is brought up here, as it
        // would be started: whatever it has connected can only be seen, and disconnected, in its window.
        var showing = FindWindow(MainTitle()) != IntPtr.Zero || FindWindow(PasswordTitle()) != IntPtr.Zero;
        if (!showing && (start || RunsHere()) && !Start(say)) return 1;
        if (!SignIn(password, say, out var code)) return code;

        var handle = FindWindow(MainTitle());
        if (handle == IntPtr.Zero)
        {
            say($"Tosi Client is not running on this desktop.{Nearby()}");
            return NotHere;
        }
        Front(handle);
        var window = AutomationElement.FromHandle(handle);
        // Just signed in, the client takes a while to fill its list.
        if (!Look(window).Any(Listed))
        {
            say("Waiting for Tosi Client to list its Locks.");
            if (!WaitUntil(() => Look(window).Any(Listed), ListFills))
            {
                say($"Tosi Client showed no list of Locks within {ListFills.TotalSeconds:0} s.{Saved(window)}");
                return 1;
            }
        }

        // The gateway is Tosi Client's only user: no Lock but the facility's stays connected - none
        // before it connects, none after it disconnects.
        if (connect) DisconnectOthers(handle, window, name, say);
        var result = SwitchOne(handle, window, name, connect, say);
        if (!connect) DisconnectOthers(handle, window, null, say);
        return result;
    }

    private static int SwitchOne(IntPtr handle, AutomationElement window, string name, bool connect, Action<string> say)
    {
        // The search is left as it is: it shows the Lock, and whatever looks next sets it as it needs.
        var search = (AutomationElement?)null;
        var row = Locate(handle, window, name, ref search, say);
        if (row is null)
        {
            say($"Tosi Client lists no Lock called '{name}', or it could not be found in its list.{Saved(window)}");
            return 1;
        }

        var state = Read(Look(window), row);
        say($"Tosi Client shows {name}: {state.Status}{(state.Button is { } shown ? $", with its {shown.Name} button" : ", with no button UI Automation sees")}.");
        if (state.Connected == connect && !state.Connecting)
        {
            say(connect ? $"{name} is connected in Tosi Client." : $"{name} is not connected in Tosi Client.");
            return 0;
        }
        if (connect && state.Connecting)
            say($"Tosi Client is connecting {name} already.");
        else if (connect && state.Button is { Enabled: false })
        {
            say($"{name} cannot be connected: {state.Status}");
            return 1;
        }
        else
        {
            // Disconnecting one still connecting is cancelling it, with whatever its button is then.
            // A blank status is taken for a connection only when it stays so: the client draws a
            // connected Lock's status itself, but the list may also be caught mid-redraw.
            if (!connect && state.Connected && state.Why == Blank)
            {
                Thread.Sleep(1000);
                if (Now(window, name) is { Connected: false } recheck)
                {
                    say($"{name} is not connected in Tosi Client.");
                    return 0;
                }
            }

            var before = state;
            var clicked = PressRow(handle, Look(window), row, state);
            say(connect ? $"Pressed Connect for {name} in Tosi Client{(clicked ? ", with the mouse, as it shows no button UI Automation sees" : "")}."
                : state.Connecting ? $"Pressed {state.Button?.Name ?? "Cancel"} for {name}, which Tosi Client was still connecting."
                : $"Pressed Disconnect for {name} in Tosi Client ({state.Why}).");

            // A client may take UI Automation's press and do nothing: when the row has not
            // changed in a few seconds, the button is clicked as a person would. Never a second
            // click, though: that would undo the first.
            if (!clicked && !WaitUntil(() => Now(window, name) is { } now && Changed(before, now), TimeSpan.FromSeconds(6)))
            {
                Front(handle);
                var seen = Look(window);
                if (Row(seen, name) is { } line && Read(seen, line) is var current && Target(seen, line, current) is { } at)
                {
                    Click(handle, at);
                    say($"Tosi Client did not take the press, so the button was clicked with the mouse.");
                }
            }
            // A click that plainly did not take - ten seconds on, the row still says it is ready to
            // connect, and nothing else has changed - is made once more. Any other change, and it
            // took: a second click then would undo it.
            else if (clicked && connect && !WaitUntil(() => Now(window, name) is { } now && Changed(before, now), TimeSpan.FromSeconds(10))
                     && Now(window, name) is { Connected: false, Connecting: false } still && NotConnected().IsMatch(still.Status))
            {
                Front(handle);
                var seen = Look(window);
                if (Row(seen, name) is { } line && Target(seen, line, Read(seen, line)) is { } at)
                {
                    Click(handle, at);
                    say($"Tosi Client still showed {name} ready to connect, so Connect was clicked once more.");
                }
            }
        }

        // Done when the client says so: its button turns, its status follows. Its list changes
        // meanwhile (a connected Lock shows its devices), so a moment it cannot be read is skipped.
        var began = DateTime.UtcNow;
        var deadline = began + (connect ? Connecting : Settle);
        var told = began;
        var lastStatus = state.Status;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (DateTime.UtcNow - told >= Heartbeat)
            {
                told = DateTime.UtcNow;
                say($"Still waiting for Tosi Client to {(connect ? "connect" : "disconnect")} {name} ({(told - began).TotalSeconds:0} s).");
            }
            try
            {
                var seen = Look(window);
                // Out of view again (someone scrolled, the list was redrawn): found again.
                if (Row(seen, name) is not { } now)
                {
                    if (Locate(handle, window, name, ref search, null) is null) continue;
                    seen = Look(window);
                    if (Row(seen, name) is not { } again) continue;
                    now = again;
                }
                state = Read(seen, now);
                if (state.Connected == connect && !state.Connecting)
                {
                    say(connect ? $"Tosi Client has connected {name}." : $"Tosi Client has disconnected {name}.");
                    return 0;
                }
                if (state.Status != lastStatus)
                {
                    lastStatus = state.Status;
                    told = DateTime.UtcNow;
                    say($"Tosi Client shows {name}: {state.Status}");
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Changed while it was read: look again.
            }
        }
        say($"Tosi Client did not show {name} {(connect ? "connected" : "disconnected")} within {(connect ? Connecting : Settle).TotalSeconds:0} s. It says: {state.Status}.{Saved(window)}");
        return 1;
    }

    // ---------------------------------------------------------------- the only user

    /// <summary>
    /// Disconnects every Lock the client has connected but <paramref name="keep"/>. The whole list is
    /// looked through, at one look or a page at a time, and a Lock counts as connected only on plain signs - its
    /// devices listed under it, a Disconnect, a status of Connected or Connecting - never a blank
    /// status alone, so one that is not connected is never pressed into connecting. Each is found
    /// again with the search, read again, and pressed once.
    /// </summary>
    private static void DisconnectOthers(IntPtr handle, AutomationElement window, string? keep, Action<string> say)
    {
        say(keep is null
            ? "Looking through Tosi Client's Locks for any still connected."
            : "Looking through Tosi Client's Locks for any other connected: the gateway is its only user.");
        var (count, connected) = ConnectedLocks(handle, window, say);
        var others = connected.Where(n => !string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)).ToList();
        say($"Tosi Client lists {count} Locks; {(others.Count > 0 ? $"connected: {string.Join(", ", others)}" : keep is null ? "none is connected" : "no other is connected")}.");

        foreach (var other in others)
        {
            AutomationElement? search = null;
            var row = Locate(handle, window, other, ref search, null);
            var seen = Look(window);
            if (row is null || Row(seen, other) is not { } line)
            {
                say($"{other} could not be found again in Tosi Client to disconnect it.");
                continue;
            }
            var state = Read(seen, line);
            if (!Surely(state)) continue;
            say($"Disconnecting {other} in Tosi Client ({state.Why}).");
            PressRow(handle, seen, line, state);
            say(WaitUntil(() => Now(window, other) is { Connected: false, Connecting: false }, Settle)
                ? $"Tosi Client has disconnected {other}."
                : $"Tosi Client did not show {other} disconnected within {Settle.TotalSeconds:0} s.");
        }
    }

    /// <summary>Connected, or connecting, by what the client plainly shows.</summary>
    private static bool Surely(State state) => state.Connecting || (state.Connected && state.Why != Blank);

    /// <summary>How many Locks the client lists, and those it shows connected or connecting.</summary>
    private static (int Count, List<string> Connected) ConnectedLocks(IntPtr handle, AutomationElement window, Action<string> say)
    {
        var seen = Look(window);
        // The whole list, not what a search left of it.
        if (SearchBox(seen) is { Value.Length: > 0 } box)
        {
            Enter(handle, box.Element, "");
            Thread.Sleep(800);
            seen = Look(window);
        }

        var column = NameColumn(seen);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connected = new List<string>();
        void ReadPage(List<Seen> page, bool anywhere = false)
        {
            column ??= NameColumn(page);
            // Each row on every page it shows on: one at the foot of a page shows its devices on the next.
            foreach (var row in Locks(page, column, anywhere))
            {
                var lockName = row.Name.Trim();
                names.Add(lockName);
                if (Surely(Read(page, row, anywhere)) && !connected.Contains(lockName, StringComparer.OrdinalIgnoreCase)) connected.Add(lockName);
            }
        }

        // A client that tells UI Automation of the rows out of view, where they are, is read from this
        // one look; only one that tells of the rows in view alone is scrolled through.
        if (Locks(seen, column, all: true).Any(row => !row.Visible))
        {
            ReadPage(seen, anywhere: true);
            return (names.Count, connected);
        }

        ReadPage(seen);
        var scroll = Locks(seen, column).Take(5).Select(row => Scroller(row.Element)).FirstOrDefault(found => found is not null);
        try
        {
            if (scroll is not null)
            {
                // A list scrolls a moment after it is told to: each page is waited for.
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                Thread.Sleep(400);
                for (var page = 0; page < 100; page++)
                {
                    ReadPage(Look(window));
                    var before = scroll.Current.VerticalScrollPercent;
                    if (before >= 99.5) break;
                    scroll.ScrollVertical(ScrollAmount.LargeIncrement);
                    if (!WaitUntil(() => Math.Abs(scroll.Current.VerticalScrollPercent - before) >= 0.01, TimeSpan.FromSeconds(2))) break;
                    Thread.Sleep(200);
                }
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            }
            else
            {
                // No scrolling the list as a whole: each row out of view brought into view in turn.
                foreach (var row in Locks(seen, column, all: true).Where(r => !r.Visible).OrderBy(r => r.Rect.Top))
                    if (!names.Contains(row.Name.Trim()) && ScrollTo(row.Element)) ReadPage(Look(window));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            // The list changed while it was scrolled: what was read stands.
            say($"Looking through Tosi Client's list stopped early: {ex.Message}");
        }
        return (names.Count, connected);
    }

    /// <summary>
    /// Where the Locks' names are: the first column, told by the rows that say they are not connected
    /// - the name is the first thing on their line. Null until some row says so.
    /// </summary>
    private static double? NameColumn(List<Seen> seen)
    {
        var lefts = new List<double>();
        foreach (var status in seen.Where(s => s.Visible && NotConnected().IsMatch(s.Name)))
        {
            var middle = status.Rect.Top + status.Rect.Height / 2;
            var name = seen
                .Where(s => s.Visible && s != status && s.Type != ControlType.Edit && s.Name.Trim().Length > 0
                    && s.Rect.Right <= status.Rect.Left + 2 && Math.Abs(s.Rect.Top + s.Rect.Height / 2 - middle) <= 12)
                .MinBy(s => s.Rect.Left);
            if (name is not null) lefts.Add(Math.Round(name.Rect.Left));
        }
        return lefts.Count == 0 ? null : lefts.GroupBy(l => l).MaxBy(g => g.Count())!.Key;
    }

    /// <summary>The Locks' rows: in the name column, and neither a device's address nor a heading.</summary>
    private static List<Seen> Locks(List<Seen> seen, double? column, bool all = false) =>
        column is not { } left ? [] :
        [
            .. seen.Where(s => (all || s.Visible) && !s.Rect.IsEmpty && Math.Abs(s.Rect.Left - left) <= 4
                && !NotARow.Contains(s.Type) && s.Name.Trim().Length > 0
                && !Address().IsMatch(s.Name.Trim()) && !NotConnected().IsMatch(s.Name) && !ConnectButton().IsMatch(s.Name.Trim())
                && !string.Equals(s.Name.Trim(), "Name", StringComparison.OrdinalIgnoreCase)),
        ];

    // What sits in the name column without being a row of the list: the window's title, its menus, headings.
    private static readonly HashSet<ControlType> NotARow =
    [
        ControlType.Edit, ControlType.Button, ControlType.TitleBar, ControlType.Window, ControlType.MenuBar, ControlType.Menu,
        ControlType.MenuItem, ControlType.ToolBar, ControlType.Header, ControlType.HeaderItem, ControlType.StatusBar,
        ControlType.ScrollBar, ControlType.Thumb, ControlType.Tab, ControlType.TabItem,
    ];

    /// <summary>What scrolls the list: the nearest parent of a row that scrolls up and down.</summary>
    private static ScrollPattern? Scroller(AutomationElement row)
    {
        var walker = TreeWalker.ControlViewWalker;
        try
        {
            for (var e = walker.GetParent(row); e is not null && e != AutomationElement.RootElement; e = walker.GetParent(e))
                if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    return (ScrollPattern)pattern;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            // Gone meanwhile.
        }
        return null;
    }

    // ---------------------------------------------------------------- starting it

    /// <summary>Some part of the client runs in this desktop session: its window, or its agent in the notification area.</summary>
    private static bool RunsHere()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                found |= process.SessionId == session && process.ProcessName.StartsWith("tosi", StringComparison.OrdinalIgnoreCase);
        }
        return found;
    }

    /// <summary>Starts the client, or brings a running one out of the notification area, and waits for its window.</summary>
    private static bool Start(Action<string> say)
    {
        var set = Environment.GetEnvironmentVariable("EASYVPN_PROGRAM");
        if (set is { Length: > 0 } && !File.Exists(set))
        {
            say($"Tosi Client is not running, and its program is set to {set}, which is not there.");
            return false;
        }
        var program = set is { Length: > 0 } ? set : Program();
        if (program is null)
        {
            say("Tosi Client is not running, and was not found to start. Set Tosi Client's program (TosiboxKey.exe) in the gateway app's Settings.");
            return false;
        }

        say($"Starting Tosi Client ({program}).");
        try
        {
            // Started in its own folder, as a shortcut would; a shortcut given instead starts it its own way.
            var shortcut = program.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            Process.Start(new ProcessStartInfo(program) { UseShellExecute = true, WorkingDirectory = shortcut ? "" : Path.GetDirectoryName(program) })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            say($"Tosi Client could not be started: {ex.Message}");
            return false;
        }
        if (!WaitUntil(() => FindWindow(MainTitle()) != IntPtr.Zero || FindWindow(PasswordTitle()) != IntPtr.Zero, TimeSpan.FromSeconds(45)))
        {
            say($"{Path.GetFileName(program)} was started, but no Tosi Client window showed within 45 s: if it is not the client, set Tosi Client's program (TosiboxKey.exe) in the gateway app's Settings.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The client's program, wherever Windows keeps track of it: a copy running in this session, the
    /// programs started with Windows, App Paths, the list of installed programs, the Start menu (a
    /// shortcut starts it as well as its program), and Program Files. Of all found, one named for
    /// a key or a client goes first: Tosibox installs other programs beside it.
    /// </summary>
    private static string? Program() =>
        Candidates().Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => ClientName().IsMatch(Path.GetFileNameWithoutExtension(p)))
            .FirstOrDefault();

    private static IEnumerable<string> Candidates()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
        {
            string? running = null;
            using (process)
            {
                try
                {
                    if (process.SessionId == session && Product().IsMatch(process.ProcessName)) running = Client(process.MainModule?.FileName ?? "");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Not ours to look at.
                }
            }
            if (running is not null) yield return running;
        }

        foreach (var (hive, view) in Registries())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using (var run = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                foreach (var value in run?.GetValueNames() ?? [])
                    if (run!.GetValue(value) is string command && (Product().IsMatch(value) || Product().IsMatch(command)) && Client(ExeOf(command)) is { } started)
                        yield return started;

            using (var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                foreach (var key in paths?.GetSubKeyNames() ?? [])
                {
                    using var entry = paths!.OpenSubKey(key);
                    if (entry?.GetValue(null) is string path && (Product().IsMatch(key) || Product().IsMatch(path)) && Client(path.Trim('"')) is { } registered)
                        yield return registered;
                }

            using var programs = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var key in programs?.GetSubKeyNames() ?? [])
            {
                using var entry = programs!.OpenSubKey(key);
                if (entry?.GetValue("DisplayName") is not string name || !Product().IsMatch(name)) continue;
                if (entry.GetValue("DisplayIcon") is string icon && Client(icon.Split(',')[0].Trim().Trim('"')) is { } fromIcon) yield return fromIcon;
                foreach (var folder in new[] { entry.GetValue("InstallLocation") as string, Path.GetDirectoryName(ExeOf(entry.GetValue("UninstallString") as string ?? "")) })
                    foreach (var inFolder in ClientsIn(folder))
                        yield return inFolder;
            }
        }

        foreach (var menu in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            var folder = Environment.GetFolderPath(menu);
            if (!Directory.Exists(folder)) continue;
            foreach (var shortcut in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .Where(l => Product().IsMatch(Path.GetFileNameWithoutExtension(l)) && !NotTheClient().IsMatch(Path.GetFileName(l))))
                yield return shortcut;
        }

        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                     .Select(Environment.GetFolderPath)
                     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
                     .Where(Directory.Exists))
            foreach (var folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true }).Where(d => Product().IsMatch(Path.GetFileName(d))))
                foreach (var inProgramFiles in ClientsIn(folder))
                    yield return inProgramFiles;
    }

    private static (RegistryHive, RegistryView)[] Registries() =>
        [(RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default)];

    /// <summary>The program a command line starts: quoted, or up to the first .exe.</summary>
    private static string ExeOf(string command) =>
        Regex.Match(command.Trim(), "^\"([^\"]+)\"|^(.+?\\.exe)", RegexOptions.IgnoreCase) is { Success: true } m
            ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            : command.Trim();

    /// <summary>The programs in a folder (and the one below) that may be the client.</summary>
    private static IEnumerable<string> ClientsIn(string? folder) =>
        string.IsNullOrEmpty(folder) || !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
                .Select(Client).OfType<string>().Where(p => ClientName().IsMatch(Path.GetFileNameWithoutExtension(p)));

    /// <summary>The path, when it is the client itself rather than its uninstaller, updater or service.</summary>
    private static string? Client(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !NotTheClient().IsMatch(Path.GetFileName(path)) ? path : null;

    // ---------------------------------------------------------------- signing in

    /// <summary>The client's password, when it asks for it - when it has just started.</summary>
    private static bool SignIn(string? password, Action<string> say, out int code)
    {
        code = 0;
        var handle = FindWindow(PasswordTitle());
        if (handle == IntPtr.Zero) return true;
        if (string.IsNullOrEmpty(password))
        {
            say("Tosi Client asks for its password: set it in the gateway app's Settings (or for this facility in the facility editor).");
            code = 1;
            return false;
        }

        var dialog = AutomationElement.FromHandle(handle);
        var box = dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (box is null || Enter(handle, box, password) is not { } typed)
        {
            say("Tosi Client's password window has no box that can be typed in.");
            code = 1;
            return false;
        }

        // Its OK button waits for the password to be typed: it stays off for text put in any other way.
        var ok = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(b => string.Equals(b.Current.Name.Trim(), "OK", StringComparison.OrdinalIgnoreCase));
        if (ok is not null && !WaitUntil(() => ok.Current.IsEnabled, TimeSpan.FromSeconds(3)))
        {
            say(typed
                ? "Tosi Client kept its OK button off after the password was typed in."
                : "Tosi Client's password window could not be brought to the front to type in, and its OK button waits for typing: the gateway's desktop must be on and unlocked.");
            code = 1;
            return false;
        }
        if (ok is not null) Press(handle, ok);
        else PressKey(0x0D); // Enter

        if (!WaitUntil(() => FindWindow(PasswordTitle()) == IntPtr.Zero, Settle))
        {
            say("Tosi Client did not take the password: check it in the gateway app's Settings.");
            code = 1;
            return false;
        }
        WaitUntil(() => FindWindow(MainTitle()) != IntPtr.Zero, Settle);
        return true;
    }

    // ---------------------------------------------------------------- what the window shows

    /// <summary>Part of the list of Locks, shown: a row of it, a Connect button, or a Lock's status.</summary>
    private static bool Listed(Seen s) =>
        s.Visible && (s.Type == ControlType.TreeItem || s.Type == ControlType.DataItem || s.Type == ControlType.ListItem
            || (s.Type == ControlType.Button && ConnectButton().IsMatch(s.Name.Trim()))
            || NotConnected().IsMatch(s.Name));

    /// <summary>The Lock's row, where it can be seen.</summary>
    private static Seen? Row(List<Seen> seen, string name) =>
        seen.FirstOrDefault(s => s.Visible && s.Type != ControlType.Edit && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The Lock's row, brought into view: found with the client's search (its whole name, then the
    /// part in brackets, then the part before), which keeps it in view whoever scrolls the list;
    /// or else where it is, scrolled to where it is listed out of view, or typed into the list
    /// itself, which goes to the row whose name starts so.
    /// </summary>
    /// <param name="say">Told how it was found, or why the search could not be used; null to say nothing.</param>
    private static Seen? Locate(IntPtr handle, AutomationElement window, string name, ref AutomationElement? search, Action<string>? say)
    {
        var seen = Look(window);
        // In view already - searched for before, by this run or the last: nothing to do.
        if (Row(seen, name) is { } kept) return kept;

        // The client's search first: a list filtered to the Lock stays put, whoever scrolls it.
        if (SearchBox(seen) is { } box)
        {
            var brackets = Regex.Match(name, @"^(.*?)\s*\[(.+)\]\s*$");
            string[] terms = brackets.Success ? [name, brackets.Groups[2].Value, brackets.Groups[1].Value] : [name];
            foreach (var term in terms.Distinct())
            {
                var entered = Enter(handle, box.Element, term);
                if (entered is null)
                {
                    say?.Invoke($"Tosi Client's search box (\"{box.Name}\" at {box.Rect.Left:0},{box.Rect.Top:0}) could not be typed into or filled. Boxes seen: {Boxes(seen)}.");
                    break;
                }
                search = box.Element;
                Seen? found = null;
                if (WaitUntil(() => (found = Row(Look(window), name)) is not null, TimeSpan.FromSeconds(3)))
                {
                    say?.Invoke($"Searched Tosi Client for \"{term}\"{(entered == true ? "" : " (put in its box, not typed)")}.");
                    return found;
                }
                say?.Invoke($"Searching Tosi Client for \"{term}\"{(entered == true ? "" : " (put in its box, not typed)")} showed no row named {name}. The box now holds \"{ValueOf(box.Element)}\".");
            }
            if (search is not null) Enter(handle, box.Element, "");
            search = null;
            seen = Look(window);
        }
        else
            say?.Invoke($"Tosi Client shows no search box UI Automation can type into, among {seen.Count} elements. Boxes seen: {Boxes(seen)}.");

        if (Row(seen, name) is { } here) return here;

        foreach (var listed in seen.Where(s => s.Type != ControlType.Edit && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            if (!ScrollTo(listed.Element)) continue;
            if (Row(Look(window), name) is { } scrolled)
            {
                say?.Invoke($"Scrolled to {name} in Tosi Client's list.");
                return scrolled;
            }
        }

        // Typed into the list: a click on a row of it, then the name.
        if (Front(handle) && seen.FirstOrDefault(s => s.Visible && (s.Type == ControlType.TreeItem || s.Type == ControlType.DataItem || s.Type == ControlType.ListItem)) is { } item)
        {
            try
            {
                Click(handle, new Point(item.Rect.Left + Math.Min(40, item.Rect.Width / 2), item.Rect.Top + item.Rect.Height / 2));
                if (GetForegroundWindow() == handle)
                {
                    TypeText(name);
                    Seen? typed = null;
                    if (WaitUntil(() => (typed = Row(Look(window), name)) is not null, TimeSpan.FromSeconds(2))) return typed;
                }
            }
            catch (InvalidOperationException)
            {
                // The desktop takes no typing.
            }
        }
        return null;
    }

    private static bool ScrollTo(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
            else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
            else return false;
            Thread.Sleep(300);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return false;
        }
    }

    /// <param name="Connecting">Neither yet: the client is still at it, and says so (or offers to cancel).</param>
    /// <param name="Why">What told it connected, or not: for the activity log.</param>
    private sealed record State(bool Connected, bool Connecting, Seen? Button, string Status, string Why = "");

    private const string Blank = "its status shows nothing UI Automation can read, as the client shows a connected Lock's";

    /// <summary>
    /// What the client shows on the row: its button, if it is one UI Automation sees, and its status.
    /// Every sign counts, as a client may show them in ways UI Automation sees differently: a
    /// Disconnect anywhere on the row (a button, its label, a cell), or a status of Connected, is
    /// connected; a Cancel, or a status of Connecting, is connecting. A button out of view does not
    /// count - the client may keep the one it replaced.
    /// </summary>
    /// <param name="anywhere">The row and what is on it, by where they are, though out of view.</param>
    private static State Read(List<Seen> seen, Seen row, bool anywhere = false)
    {
        bool Here(Seen s) => anywhere ? !s.Rect.IsEmpty : s.Visible;

        // The row's own line: an opened row may measure as tall as the devices listed under it.
        var top = row.Rect.Top;
        var bottom = row.Rect.Top + Math.Min(row.Rect.Height, 48);
        bool OnLine(Seen s) => Here(s) && s.Rect.Top + s.Rect.Height / 2 >= top && s.Rect.Top + s.Rect.Height / 2 <= bottom;

        var line = seen.Where(OnLine).ToList();
        var labels = line.Select(s => s.Name.Trim()).Where(text => text.Length > 0).ToList();
        bool Shows(string word) => labels.Any(l => string.Equals(l, word, StringComparison.OrdinalIgnoreCase));

        var buttons = line.Where(s => s.Type == ControlType.Button && ConnectButton().IsMatch(s.Name.Trim())).ToList();
        var button = buttons.FirstOrDefault(b => b.Name.Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
            ?? buttons.FirstOrDefault(b => b.Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
            ?? buttons.FirstOrDefault();
        var status = string.Join(" ", line
            .Where(s => s.Type != ControlType.Button && s.Type != ControlType.Edit)
            .Select(s => s.Name.Trim())
            // Not the Lock's own name, nor a button's label inside the button.
            .Where(text => text.Length > 0 && !string.Equals(text, row.Name.Trim(), StringComparison.OrdinalIgnoreCase) && !ConnectButton().IsMatch(text))
            .Distinct());

        // The Lock's devices, listed under its row (indented, by address) once it is connected.
        var devices = seen.Count(s => Here(s) && s.Rect.Left > row.Rect.Left + 4
            && s.Rect.Top >= bottom - 2 && s.Rect.Top <= bottom + 3 * 48 && Address().IsMatch(s.Name.Trim()));

        // Not connected, for sure: the status says so (ready to connect, no Internet connection …).
        var idle = NotConnected().IsMatch(status);
        var connecting = Shows("Cancel") || ConnectingNow().IsMatch(status);
        var why = idle ? $"it says {status}"
            : Shows("Disconnect") ? "it shows Disconnect"
            : Connected().IsMatch(status) ? $"it says {status}"
            : devices > 0 ? "its devices are listed under it"
            : status.Length == 0 ? Blank
            : $"it says {status}";
        var connected = !connecting && !idle && (Shows("Disconnect") || Connected().IsMatch(status) || devices > 0 || status.Length == 0);
        return new State(connected, connecting, button, status.Length > 0 ? status : "(no status shown)", why);
    }

    /// <summary>The Lock's row as it is now, where it can be seen; null where it cannot.</summary>
    private static State? Now(AutomationElement window, string name)
    {
        var seen = Look(window);
        return Row(seen, name) is { } row ? Read(seen, row) : null;
    }

    private static bool Changed(State before, State now) =>
        now.Connected != before.Connected || now.Connecting != before.Connecting
        || now.Status != before.Status || now.Button?.Name != before.Button?.Name;

    /// <summary>Where a person would click on the row: its button, or where the Connection column crosses it.</summary>
    private static Point? Target(List<Seen> seen, Seen row, State state)
    {
        var middle = row.Rect.Top + Math.Min(row.Rect.Height, 48) / 2;
        if (state.Button is { } button) return new Point(button.Rect.Left + button.Rect.Width / 2, button.Rect.Top + button.Rect.Height / 2);
        return seen.FirstOrDefault(s => s.Visible && string.Equals(s.Name.Trim(), "Connection", StringComparison.OrdinalIgnoreCase)) is { } column
            ? new Point(column.Rect.Left + column.Rect.Width / 2, middle)
            : null;
    }

    /// <summary>The row's button, or where the Connection column crosses the row when the client draws its buttons itself.</summary>
    /// <returns>True when it was a click with the mouse, rather than UI Automation's press.</returns>
    private static bool PressRow(IntPtr window, List<Seen> seen, Seen row, State state)
    {
        if (state.Button is { } button && button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return false;
        }
        var at = Target(seen, row, state)
            ?? throw new InvalidOperationException("Tosi Client shows no Connect button, and no Connection column to find one in.");
        Click(window, at);
        return true;
    }

    /// <summary>The search box: one that says so, or else the box shown highest (and furthest right) in the window.</summary>
    private static Seen? SearchBox(List<Seen> seen)
    {
        // Only one that takes typing: a client may show read-only text (a status line) as a box too.
        var boxes = seen.Where(s => s.Type == ControlType.Edit && s.Visible && !s.ReadOnly).ToList();
        return boxes.FirstOrDefault(b => (b.Name + b.Help).Contains("Search", StringComparison.OrdinalIgnoreCase))
            ?? boxes.OrderByDescending(b => b.Rect.Left).ThenBy(b => b.Rect.Top).FirstOrDefault();
    }

    /// <summary>Every box the window shows UI Automation, for working out why the search could not be used.</summary>
    private static string Boxes(List<Seen> seen)
    {
        var boxes = seen.Where(s => s.Type == ControlType.Edit).Select(b =>
            $"\"{b.Name}\" holding \"{b.Value}\" at {b.Rect.Left:0},{b.Rect.Top:0}{(b.ReadOnly ? ", read-only" : "")}{(b.Visible ? "" : ", hidden")}").ToList();
        return boxes.Count == 0 ? "none" : string.Join("; ", boxes);
    }

    // ---------------------------------------------------------------- what it sees, written down

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

    /// <summary>What was seen of the client, saved for working out why: " It is in …", or nothing when it could not be.</summary>
    private static string Saved(AutomationElement? window = null)
    {
        try
        {
            var lines = new List<string>();
            if (window is not null) Write(window, 0, lines.Add);
            else Dump(lines.Add);
            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath)!);
            File.WriteAllLines(DumpPath, lines);
            return $" What it showed is in {DumpPath} on the gateway.";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- Windows

    [GeneratedRegex(@"^(Tosi|TOSIBOX) Key\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainTitle();

    [GeneratedRegex(@"^(Tosi|TOSIBOX)\b.*password", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordTitle();

    [GeneratedRegex(@"^((Dis)?connect|Cancel)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectButton();

    [GeneratedRegex(@"\bconnecting\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectingNow();

    /// <summary>Tosi, TOSIBOX, TosiboxKey …</summary>
    [GeneratedRegex(@"\btosi", RegexOptions.IgnoreCase)]
    private static partial Regex Product();

    // TosiboxKeyAgent.exe, which Tosibox starts at sign-in, shows no window of its own: started
    // instead of TosiboxKey.exe, it left the gateway waiting for one.
    [GeneratedRegex(@"unins|uninstall|update|service|agent|helper|crash|driver|tapctl|devcon|setup|openvpn|redist", RegexOptions.IgnoreCase)]
    private static partial Regex NotTheClient();

    /// <summary>The client among Tosibox's programs: TosiboxKey.exe, or one called a client.</summary>
    [GeneratedRegex(@"key|client", RegexOptions.IgnoreCase)]
    private static partial Regex ClientName();

    [GeneratedRegex(@"\bconnected\b", RegexOptions.IgnoreCase)]
    private static partial Regex Connected();

    /// <summary>A status that says it is not connected: ready to connect, no Internet connection, offline …</summary>
    [GeneratedRegex(@"\b(not|dis)\s*connected\b|ready to connect|no internet|offline|unreachable|not available", RegexOptions.IgnoreCase)]
    private static partial Regex NotConnected();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex Address();

    /// <summary>
    /// What there is of Tosi on this desktop when its windows are not as expected: the titles of
    /// windows, and the programs, named for it - " Here: …", or nothing.
    /// </summary>
    private static string Nearby()
    {
        var titles = new List<string>();
        EnumWindows((handle, _) =>
        {
            var text = new StringBuilder(256);
            GetWindowText(handle, text, text.Capacity);
            if (Product().IsMatch(text.ToString())) titles.Add($"\"{text}\"{(IsWindowVisible(handle) ? "" : " (hidden)")}");
            return true;
        }, IntPtr.Zero);
        var session = Process.GetCurrentProcess().SessionId;
        var programs = Process.GetProcesses().Where(p => p.SessionId == session && Product().IsMatch(p.ProcessName)).Select(p => p.ProcessName).Distinct().ToList();
        if (titles.Count == 0 && programs.Count == 0) return "";
        return $" Here: windows {(titles.Count > 0 ? string.Join(", ", titles.Distinct()) : "none")}; programs {(programs.Count > 0 ? string.Join(", ", programs) : "none")}.";
    }
}
