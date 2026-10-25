using System.ComponentModel;
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
        var showing = FindWindow(MainTitle()) != IntPtr.Zero || FindWindow(PasswordTitle()) != IntPtr.Zero;
        if (!showing && start && !Start(say)) return 1;
        if (!SignIn(password, say, out var code)) return code;

        var handle = FindWindow(MainTitle());
        if (handle == IntPtr.Zero)
        {
            say($"Tosi Client is not running on this desktop.{Nearby()}");
            return NotHere;
        }
        Front(handle);
        var window = AutomationElement.FromHandle(handle);
        var search = (AutomationElement?)null;
        try
        {
            // Just signed in, the client takes a while to fill its list.
            if (!WaitUntil(() => Look(window).Any(Listed), ListFills))
            {
                say($"Tosi Client showed no list of Locks within {ListFills.TotalSeconds:0} s.{Saved(window)}");
                return 1;
            }
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
                var clicked = Press(Look(window), row, state);
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
                        Click(at);
                        say($"Tosi Client did not take the press, so the button was clicked with the mouse.");
                    }
                }
            }

            // Done when the client says so: its button turns, its status follows. Its list changes
            // meanwhile (a connected Lock shows its devices), so a moment it cannot be read is skipped.
            var deadline = DateTime.UtcNow + (connect ? Connecting : Settle);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
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
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                {
                    // Changed while it was read: look again.
                }
            }
            say($"Tosi Client did not show {name} {(connect ? "connected" : "disconnected")} within {(connect ? Connecting : Settle).TotalSeconds:0} s. It says: {state.Status}.{Saved(window)}");
            return 1;
        }
        finally
        {
            // Leave the list as a person left it.
            if (search is not null) Enter(handle, search, "");
        }
    }

    // ---------------------------------------------------------------- starting it

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
        Thread.Sleep(1500); // Its list fills in a moment later.
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
        if (ok is not null) Press(ok);
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

    /// <summary>One element of the window as it was when looked at.</summary>
    private sealed record Seen(AutomationElement Element, string Name, ControlType Type, Rect Rect, bool Offscreen, bool Enabled, string Help,
        bool ReadOnly = false, string Value = "")
    {
        public bool Visible => !Offscreen && !Rect.IsEmpty;
    }

    /// <summary>
    /// Every element of the window, with what is needed of each fetched in one go: quick even when a
    /// connected Lock lists a hundred devices, and steady while the client changes underneath.
    /// </summary>
    private static List<Seen> Look(AutomationElement window)
    {
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty, AutomationElement.HelpTextProperty,
                     ValuePattern.IsReadOnlyProperty, ValuePattern.ValueProperty })
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
                seen.Add(new Seen(e, c.Name ?? "", c.ControlType, rect, c.IsOffscreen || !inside, c.IsEnabled, c.HelpText ?? "", readOnly, value));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
            {
                // Gone already.
            }
        }
        return seen;
    }

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
        // Searched for already, and still there.
        if (search is not null && Row(seen, name) is { } kept) return kept;

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
                Click(new Point(item.Rect.Left + Math.Min(40, item.Rect.Width / 2), item.Rect.Top + item.Rect.Height / 2));
                if (GetForegroundWindow() == handle)
                {
                    Type(name);
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
    private static State Read(List<Seen> seen, Seen row)
    {
        // The row's own line: an opened row may measure as tall as the devices listed under it.
        var top = row.Rect.Top;
        var bottom = row.Rect.Top + Math.Min(row.Rect.Height, 48);
        bool OnLine(Seen s) => s.Visible && s.Rect.Top + s.Rect.Height / 2 >= top && s.Rect.Top + s.Rect.Height / 2 <= bottom;

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
        var devices = seen.Count(s => s.Visible && s.Rect.Left > row.Rect.Left + 4
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
    private static bool Press(List<Seen> seen, Seen row, State state)
    {
        if (state.Button is { } button && button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return false;
        }
        var at = Target(seen, row, state)
            ?? throw new InvalidOperationException("Tosi Client shows no Connect button, and no Connection column to find one in.");
        Click(at);
        return true;
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

    /// <summary>
    /// Puts text in a box as a person does - a click into it, the text typed - as the client reacts
    /// to typing alone (its OK button, its search). Typed only once its window is in front, so
    /// nothing lands anywhere else, and read back where it can be. Otherwise put there through UI
    /// Automation.
    /// </summary>
    /// <returns>True when typed, false when put there otherwise, null when neither would do.</returns>
    private static bool? Enter(IntPtr window, AutomationElement box, string text)
    {
        try
        {
            Front(window);
            // A click focuses the box as surely as a person's, and brings the window to the front
            // with it; UI Automation's focus as well, should the click not get there.
            var r = box.Current.BoundingRectangle;
            if (!r.IsEmpty && !box.Current.IsOffscreen) TryClick(new Point(r.Left + Math.Min(r.Width - 4, Math.Max(8, r.Width / 2)), r.Top + r.Height / 2));
            if (GetForegroundWindow() != window) Front(window);
            box.SetFocus();
            if (GetForegroundWindow() == window)
            {
                SelectAll();
                PressKey(0x08); // Backspace
                Type(text);
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
    private static string? ValueOf(AutomationElement box)
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

    private static bool WaitUntil(Func<bool> done, TimeSpan within)
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

    [GeneratedRegex(@"^((Dis)?connect|Cancel)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectButton();

    [GeneratedRegex(@"\bconnecting\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectingNow();

    /// <summary>Tosi, TOSIBOX, TosiboxKey …</summary>
    [GeneratedRegex(@"\btosi", RegexOptions.IgnoreCase)]
    private static partial Regex Product();

    [GeneratedRegex(@"unins|uninstall|update|service|helper|crash|driver|tapctl|devcon|setup|openvpn|redist", RegexOptions.IgnoreCase)]
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

    /// <summary>
    /// Shown, restored and in front, so the client lays out its list and takes typing. Windows lets
    /// only the program in front give the front away, so this borrows its input for a moment.
    /// </summary>
    /// <returns>True when the window is in front.</returns>
    private static bool Front(IntPtr handle)
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

    private static void Click(Point at)
    {
        SetCursorPos((int)at.X, (int)at.Y);
        Send(Mouse(0x0002), Mouse(0x0004)); // left down, left up
        Thread.Sleep(250);
    }

    private static void TryClick(Point at)
    {
        try
        {
            Click(at);
        }
        catch (InvalidOperationException)
        {
            // No mouse to be had here; the other ways will do.
        }
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
