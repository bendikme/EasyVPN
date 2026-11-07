using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
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
/// the mouse and keyboard where it does not - which needs the session's screen unlocked. The client
/// is left listing every Lock (its search emptied), as Tosibox Monitor reads its list, and its
/// "Access rights changed" - Locks given to the key, or taken - is answered with OK.
///
///   EasyVPN.GatewayApp.exe --tosibox connect "Tosibox 15 [Plant]" [--start] [--fresh]   the password in EASYVPN_PASSWORD
///   EasyVPN.GatewayApp.exe --tosibox disconnect "Tosibox 15 [Plant]"
///   EasyVPN.GatewayApp.exe --tosibox dump > tosi.txt                     what it sees of the client
///   EasyVPN.GatewayApp.exe --tosibox watch "Tosibox 15 [Plant]" [seconds]  what it shows of the Lock, as it changes
///
/// With --start, a client that shows no window here is started: from EASYVPN_PROGRAM, or from
/// wherever Windows keeps track of it. Started again, a running one that sits in the notification
/// area shows itself. With --fresh (or EASYVPN_FRESH=1, from the gateway), a Lock connected
/// already is disconnected and connected again: the gateway learns its networks from what appears
/// while it connects. When the Lock cannot be found or read, what was seen of the client is saved
/// to <see cref="DumpPath"/>, with a picture of the screen beside it, and said.
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

    // A window that changes while it is read - just started, shown from the notification area,
    // redrawn - makes UI Automation fail now and then: looked at again, a few times, before giving up.
    private const int Attempts = 3;

    public static int Run(string[] args, Action<string> say)
    {
        var password = Environment.GetEnvironmentVariable("EASYVPN_PASSWORD");
        var fresh = Environment.GetEnvironmentVariable("EASYVPN_FRESH") == "1";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return args switch
                {
                    ["connect", var name, .. var options] => Switch(name.Trim(), connect: true, password, options.Contains("--start"), fresh || options.Contains("--fresh"), say),
                    ["disconnect", var name] => Switch(name.Trim(), connect: false, password, start: false, fresh: false, say),
                    ["dump"] => Dump(say),
                    ["watch", var name, .. var rest] => Watch(name.Trim(), rest is [var s, ..] && int.TryParse(s, out var seconds) ? seconds : 120, say),
                    _ => Usage(say),
                };
            }
            catch (Exception ex) when (attempt < Attempts && ex is ElementNotAvailableException or COMException or ElementNotEnabledException)
            {
                // Started on again from the top: it reads the Lock before pressing anything, so a
                // press made already is seen, not made twice.
                say($"Tosi Client changed while it was read ({ex.GetType().Name}: {ex.Message}); looking again.");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            catch (NoInputException ex)
            {
                // The gateway brings the desktop to its console, where it does, and asks again.
                say($"{ex.Message}{Saved()}");
                return NoInput;
            }
            catch (Exception ex)
            {
                // Never a crash: said, and written down whole.
                CrashLog.Write(ex, "driving Tosi Client");
                say($"Tosi Client could not be driven ({ex.GetType().Name}: {ex.Message}).{Saved()} The details are in {CrashLog.Path} on the gateway.");
                return 1;
            }
        }
    }

    private static int Usage(Action<string> say)
    {
        say("--tosibox connect <Lock> [--start] [--fresh] | disconnect <Lock> | dump | watch <Lock> [seconds]");
        return 1;
    }

    // ---------------------------------------------------------------- connect, disconnect

    private static int Switch(string name, bool connect, string? password, bool start, bool fresh, Action<string> say)
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
        try
        {
            // Access rights given to the key while it was away come in a dialog over the window,
            // which takes nothing else until it is answered.
            if (!Answered(handle, say)) return 1;
            // Just signed in, the client takes a while to fill its list.
            if (!Look(window).Any(Listed))
            {
                say("Waiting for Tosi Client to list its Locks.");
                if (!WaitUntil(() => Look(window).Any(Listed), ListFills))
                {
                    say($"Tosi Client showed no list of Locks within {ListFills.TotalSeconds:0} s.{Saved(window)}");
                    return 1;
                }
                if (!Answered(handle, say)) return 1;
            }

            // The gateway is Tosi Client's only user: no Lock but the facility's stays connected - none
            // before it connects, none after it disconnects.
            if (connect) DisconnectOthers(handle, window, name, say);
            var result = SwitchOne(handle, window, name, connect, say, fresh);
            if (!connect) DisconnectOthers(handle, window, null, say);
            return result;
        }
        finally
        {
            // Whatever came of it, the client is left listing every Lock: Tosibox Monitor reads its list.
            ClearSearch(handle, window, say);
        }
    }

    /// <param name="fresh">A Lock connected already is disconnected first, then connected.</param>
    private static int SwitchOne(IntPtr handle, AutomationElement window, string name, bool connect, Action<string> say, bool fresh = false)
    {
        // The search stays while the Lock is switched, keeping its row in view; Switch empties it after.
        var search = (AutomationElement?)null;
        var row = Locate(handle, window, name, ref search, say);
        if (row is null)
        {
            say($"Tosi Client lists no Lock called '{name}', or it could not be found in its list.{Saved(window)}");
            return 1;
        }

        var state = Read(Look(window), row);
        say($"Tosi Client shows {name}: {state.Status}{(state.Button is { } shown ? $", with its {shown.Name} button" : ", with no button UI Automation sees")}.");

        // Still disconnecting, from before: let it finish, then go on from where it is.
        if (state.Leaving)
        {
            say($"Tosi Client is still disconnecting {name}: waiting for it to finish.");
            if (!WaitUntil(() => Now(window, name) is { Leaving: false }, Settle))
            {
                say($"Tosi Client was still disconnecting {name} after {Settle.TotalSeconds:0} s.{Saved(window)}");
                return 1;
            }
            if (Now(window, name) is { } settled) state = settled;
            say($"Tosi Client shows {name}: {state.Status}.");
        }

        // Up already, and the gateway must see it come up to learn its networks: taken down first.
        if (connect && fresh && state is { Connected: true, Connecting: false } && Surely(state))
        {
            say($"{name} is connected already, and the gateway has to see it connect to learn its networks: disconnecting it first.");
            if (SwitchOne(handle, window, name, connect: false, say) != 0) return 1;
            return SwitchOne(handle, window, name, connect: true, say);
        }

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
        // A blank status passes for connected only once it has held a while, or the client's
        // adapter has come up: the list may be caught mid-redraw between one status and the next.
        DateTime? blankSince = null;
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
                // Access rights that come in while it connects are answered: the client takes nothing until they are.
                if (!IsWindowEnabled(handle)) Acknowledge(handle, say);
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
                var settled = state.Connected == connect && !state.Connecting && !state.Leaving;
                if (settled && connect && state.Why == Blank)
                {
                    blankSince ??= DateTime.UtcNow;
                    settled = DateTime.UtcNow - blankSince >= TimeSpan.FromSeconds(3) || Adapter() is { Up: true };
                }
                else blankSince = null;
                // Its row shows neither way, nor under way, and the client's adapter is up: the
                // others were disconnected before, so it is this Lock that is up.
                if (!settled && connect && !state.Connecting && !state.Leaving && !state.Idle && Adapter() is { Up: true })
                {
                    settled = true;
                    state = state with { Why = $"it says {state.Status}, and Tosi Client's adapter is up" };
                }
                if (settled)
                {
                    say(connect ? $"Tosi Client has connected {name} ({state.Why}; its adapter is {AdapterSays()})." : $"Tosi Client has disconnected {name}.");
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
            say(WaitUntil(() => Now(window, other) is { Connected: false, Connecting: false, Leaving: false }, Settle)
                ? $"Tosi Client has disconnected {other}."
                : $"Tosi Client did not show {other} disconnected within {Settle.TotalSeconds:0} s.");
        }
    }

    /// <summary>Connected, or connecting, by what the client plainly shows.</summary>
    /// <remarks>
    /// A blank status counts only while the client's adapter is up: some Lock is connected then,
    /// and the client shows a connected Lock's status as a picture, which UI Automation reads as nothing.
    /// </remarks>
    private static bool Surely(State state) => state.Connecting || (state.Connected && (state.Why != Blank || Adapter() is { Up: true }));

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

    // ---------------------------------------------------------------- left as found: its list, its dialogs

    /// <summary>
    /// The client's search emptied, so its list shows every Lock again. Tosibox Monitor reads the list
    /// as it stands, and a search left in it hides every other Lock from it. A run that needs its Lock
    /// in view searches for it again.
    /// </summary>
    private static void ClearSearch(IntPtr handle, AutomationElement window, Action<string> say)
    {
        try
        {
            // A dialog over the window takes the typing.
            if (!IsWindowEnabled(handle)) Acknowledge(handle, say);
            if (SearchBox(Look(window)) is not { Value.Length: > 0 } box) return;
            if (Enter(handle, box.Element, "") is null || ValueOf(box.Element) is { Length: > 0 })
                say($"Tosi Client's search could not be emptied: it still holds \"{ValueOf(box.Element) ?? box.Value}\", so its list, and Tosibox Monitor, show only the Locks it finds.");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            say($"Tosi Client's search could not be emptied ({ex.Message}): Tosibox Monitor sees only the Locks it shows.");
        }
    }

    /// <summary>What is done with a dialog the client shows over its window.</summary>
    public enum Answer { Leave, Wait, Press }

    /// <summary>Nothing in the way of the client's window: what it may answer is answered, and anything else said.</summary>
    private static bool Answered(IntPtr handle, Action<string> say)
    {
        if (Acknowledge(handle, say) is not { } open) return true;
        say($"Tosi Client shows {open} over its window, and takes nothing else until someone answers it on the gateway's desktop.{Saved()}");
        return false;
    }

    /// <summary>
    /// Answers the client's "Access rights changed" - Locks given to its key by a Master Key, or
    /// taken from it - which waits over its window for a person: until it is answered the client
    /// takes nothing else, and Tosibox Monitor can only report it. Every Lock it offers is ticked
    /// and its OK pressed: the gateway takes them all, as its facilities are made from them and
    /// Tosibox Monitor watches them. Nothing else is ever pressed: what a person must decide (Accept
    /// or Reject, Remove or Cancel) is left to a person.
    /// </summary>
    /// <returns>Null when nothing is in the way; otherwise the dialog that is, for saying so.</returns>
    private static string? Acknowledge(IntPtr main, Action<string> say)
    {
        var deadline = DateTime.UtcNow + Settle;
        var pressed = new HashSet<IntPtr>();
        while (true)
        {
            string? open = null, waitedOn = null;
            foreach (var dialog in OtherWindows(main))
            {
                var title = Title(dialog);
                if (PasswordTitle().IsMatch(title)) continue;
                try
                {
                    var seen = Look(AutomationElement.FromHandle(dialog));
                    var texts = seen.Where(s => s.Visible && s.Type == ControlType.Text && s.Name.Trim().Length > 0).Select(s => s.Name.Trim()).Distinct().ToList();
                    var buttons = seen.Where(s => s.Visible && s.Type == ControlType.Button && Label(s.Name).Length > 0).ToList();
                    var (answer, label) = Answering(title, texts, [.. buttons.Select(b => (b.Name, b.Enabled))]);
                    var shown = Shown(title, texts, buttons.Select(b => b.Name));
                    if (answer == Answer.Press && !pressed.Contains(dialog))
                    {
                        var offered = seen.Where(Offered).Select(s => s.Name.Trim()).Where(n => n.Length > 0).Distinct().ToList();
                        var (ticked, unticked) = Granted().IsMatch(title) || texts.Any(Granted().IsMatch) ? Tick(seen) : ([], []);
                        Press(dialog, buttons.First(b => b.Name == label).Element);
                        pressed.Add(dialog);
                        say($"Tosi Client showed {shown}{(offered.Count > 0 ? $", listing {string.Join(", ", offered)}" : "")}. Pressed {Label(label!)}"
                            + (ticked.Count > 0 ? $", after ticking {string.Join(", ", ticked)}" : "")
                            + (unticked.Count > 0 ? $"; {string.Join(", ", unticked)} could not be ticked, so the key does not take them" : "") + ".");
                    }
                    // Pressed and closing, still receiving, or its OK not yet on: looked at again.
                    if (answer != Answer.Leave) waitedOn = shown;
                    // Anything else is in the way only while the window waits on it.
                    else if (!IsWindowEnabled(main)) open ??= shown;
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or ElementNotEnabledException
                                           or (InvalidOperationException and not NoInputException))
                {
                    // Closed, or changed, while it was read or pressed: looked at again.
                    waitedOn ??= $"\"{title}\"";
                }
            }
            if (waitedOn is null) return open;
            if (DateTime.UtcNow >= deadline) return open ?? waitedOn;
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// What is done with a dialog of the client's. "Access rights changed" is answered with its OK, or
    /// with its one button when that one does not refuse; while it is still receiving the changes, or
    /// its OK is off, it is waited for. Anything else is left.
    /// </summary>
    /// <param name="texts">What it says: its title aside.</param>
    /// <param name="buttons">Its buttons' labels, and whether each takes a press.</param>
    /// <returns>What to do, and the label of the button to press.</returns>
    public static (Answer Answer, string? Button) Answering(string title, IReadOnlyList<string> texts, IReadOnlyList<(string Name, bool Enabled)> buttons)
    {
        if (!AccessRights().IsMatch(title) && !texts.Any(AccessRights().IsMatch)) return (Answer.Leave, null);
        (string Name, bool Enabled)? choice =
            buttons.FirstOrDefault(b => string.Equals(Label(b.Name), "OK", StringComparison.OrdinalIgnoreCase)) is { Name: not null } ok ? ok
            : buttons is [var only] && !Refusing().IsMatch(Label(only.Name)) ? only
            : null;
        if (choice is not { } button)
            return (buttons.Count == 0 || Receiving().IsMatch(title) || texts.Any(Receiving().IsMatch) ? Answer.Wait : Answer.Leave, null);
        return button.Enabled ? (Answer.Press, button.Name) : (Answer.Wait, null);
    }

    /// <summary>A button's label as it reads: without the &amp; that marks its key.</summary>
    private static string Label(string name) => name.Replace("&", "").Trim();

    /// <summary>A Lock a dialog lists.</summary>
    private static bool Offered(Seen s) =>
        s.Type == ControlType.TreeItem || s.Type == ControlType.DataItem || s.Type == ControlType.ListItem || s.Type == ControlType.CheckBox;

    /// <summary>Every Lock offered ticked, so OK takes them all. Each is read as it is now: ticking a Lock ticks what is under it.</summary>
    /// <returns>Those ticked here, and those that could not be.</returns>
    private static (List<string> Ticked, List<string> Unticked) Tick(List<Seen> seen)
    {
        var ticked = new List<string>();
        var unticked = new List<string>();
        foreach (var item in seen.Where(Offered))
        {
            try
            {
                if (!item.Element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)) continue;
                var toggle = (TogglePattern)pattern;
                if (toggle.Current.ToggleState != ToggleState.Off) continue;
                toggle.Toggle();
                Thread.Sleep(150);
                (toggle.Current.ToggleState == ToggleState.Off ? unticked : ticked).Add(item.Name.Trim());
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                unticked.Add(item.Name.Trim());
            }
        }
        return (ticked, unticked);
    }

    /// <summary>A dialog, for saying what it showed: its title, what it says, its buttons.</summary>
    private static string Shown(string title, List<string> texts, IEnumerable<string> buttons)
    {
        var said = string.Join(" ", texts);
        if (said.Length > 300) said = said[..300] + "…";
        var labels = buttons.Select(Label).ToList();
        return $"\"{title}\"{(said.Length > 0 ? $" saying \"{said}\"" : "")}, {(labels.Count > 0 ? $"with {string.Join(", ", labels)}" : "with no button")}";
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
        // In view already (searched for before, in this run): nothing to do.
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
    /// <param name="Leaving">The client is disconnecting it, and says so.</param>
    /// <param name="Idle">The client says, in so many words, that it is not connected.</param>
    private sealed record State(bool Connected, bool Connecting, Seen? Button, string Status, string Why = "", bool Leaving = false, bool Idle = false);

    /// <summary>What a Lock's status says of it, in words.</summary>
    public enum Says { Nothing, NotConnected, Connecting, Disconnecting, Connected }

    /// <summary>
    /// A Lock's status, from the cells on its row (but its name): what they say, without its
    /// addresses (the MAC column, the IP column) or a button's label. Blank for a connected Lock:
    /// Tosi Client draws "Connected" there as a picture, which UI Automation reads as nothing.
    /// </summary>
    public static string Status(IEnumerable<string> cells) =>
        string.Join(" ", cells.Select(c => c.Trim())
            .Where(text => text.Length > 0 && !Address().IsMatch(text) && !Hardware().IsMatch(text) && !ConnectButton().IsMatch(text))
            .Distinct());

    /// <summary>
    /// What a Lock's status says of it: Tosi Client goes through Connecting..., Authenticating...
    /// and Assigning IP address... on the way up - anything it shows with an ellipsis is under way -
    /// and Disconnecting... on the way down. The Lock's address may stand in front of it.
    /// </summary>
    public static Says Reading(string status) =>
        Disconnecting().IsMatch(status) ? Says.Disconnecting
        : NotConnected().IsMatch(status) ? Says.NotConnected
        : ConnectingNow().IsMatch(status) ? Says.Connecting
        : Connected().IsMatch(status) ? Says.Connected
        : Says.Nothing;

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
        var status = Status(line
            .Where(s => s.Type != ControlType.Button && s.Type != ControlType.Edit)
            .Select(s => s.Name)
            // Not the Lock's own name.
            .Where(text => !string.Equals(text.Trim(), row.Name.Trim(), StringComparison.OrdinalIgnoreCase)));

        // The Lock's devices, listed under its row (indented, by address) once it is connected.
        var devices = seen.Count(s => Here(s) && s.Rect.Left > row.Rect.Left + 4
            && s.Rect.Top >= bottom - 2 && s.Rect.Top <= bottom + 3 * 48 && Address().IsMatch(s.Name.Trim()));

        // Not connected, for sure: the status says so (ready to connect, no Internet connection …).
        var says = Reading(status);
        var idle = says == Says.NotConnected;
        var leaving = says == Says.Disconnecting;
        var connecting = !leaving && (Shows("Cancel") || says == Says.Connecting);
        var why = idle || leaving ? $"it says {status}"
            : Shows("Disconnect") ? "it shows Disconnect"
            : says == Says.Connected ? $"it says {status}"
            : devices > 0 ? "its devices are listed under it"
            : status.Length == 0 ? Blank
            : $"it says {status}";
        var connected = !connecting && !idle && !leaving && (Shows("Disconnect") || says == Says.Connected || devices > 0 || status.Length == 0);
        return new State(connected, connecting, button, status.Length > 0 ? status : "(no status shown)", why, leaving, idle);
    }

    /// <summary>The Lock's row as it is now, where it can be seen; null where it cannot.</summary>
    private static State? Now(AutomationElement window, string name)
    {
        var seen = Look(window);
        return Row(seen, name) is { } row ? Read(seen, row) : null;
    }

    private static bool Changed(State before, State now) =>
        now.Connected != before.Connected || now.Connecting != before.Connecting || now.Leaving != before.Leaving
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

    /// <summary>A picture of the screen, beside <see cref="DumpPath"/>.</summary>
    public static string PicturePath { get; } = Path.ChangeExtension(DumpPath, ".png");

    private static int Dump(Action<string> say)
    {
        var found = false;
        var shown = IntPtr.Zero;
        foreach (var title in new[] { MainTitle(), PasswordTitle() })
        {
            var handle = FindWindow(title);
            if (handle == IntPtr.Zero) continue;
            found = true;
            say($"[{Title(handle)}: {(IsWindowVisible(handle) ? "shown" : "hidden")}]");
            Write(AutomationElement.FromHandle(handle), 0, say);
            shown = handle;
        }
        // Its dialogs, over its window.
        if (FindWindow(MainTitle()) is var main && main != IntPtr.Zero)
            foreach (var dialog in OtherWindows(main).Where(d => !PasswordTitle().IsMatch(Title(d))))
            {
                say($"[{Title(dialog)}: a dialog of Tosi Client's{(IsWindowEnabled(main) ? "" : ", its window taking nothing until it is answered")}]");
                Write(AutomationElement.FromHandle(dialog), 0, say);
            }
        if (!found) say($"No Tosi Client window on this desktop.{Nearby()}");
        say($"[Adapter: {(Adapter() is { } adapter ? $"{adapter.Name}, {(adapter.Up ? "up" : "down")} {adapter.Addresses}" : "none named for Tosi")}]");
        if (Snap(PicturePath, shown)) say($"[A picture of the screen is in {PicturePath}.]");
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
            lines.Add($"[Adapter: {AdapterSays()}]");
            Directory.CreateDirectory(Path.GetDirectoryName(DumpPath)!);
            File.WriteAllLines(DumpPath, lines);
            var handle = window is null ? FindWindow(MainTitle()) : new IntPtr(window.Current.NativeWindowHandle);
            var picture = Snap(PicturePath, handle) ? $", and a picture of the screen in {PicturePath}" : "";
            return $" What it showed is in {DumpPath} on the gateway{picture}.";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Watches a Lock's row, and the client's adapter, without pressing anything: each time what
    /// they show changes, it is said, and the client's window and the screen saved as they are
    /// then, numbered, in a folder beside <see cref="DumpPath"/>. For working out what the client
    /// shows on the way up and down: press Connect or Disconnect while it watches.
    /// </summary>
    private static int Watch(string name, int seconds, Action<string> say)
    {
        var handle = FindWindow(MainTitle());
        if (handle == IntPtr.Zero)
        {
            say($"Tosi Client shows no window on this desktop.{Nearby()}");
            return NotHere;
        }
        var window = AutomationElement.FromHandle(handle);
        var folder = Path.Combine(Path.GetDirectoryName(DumpPath)!, "tosi-watch");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var log = new List<string>();
        void Tell(string line)
        {
            say(line);
            log.Add(line);
            File.WriteAllLines(Path.Combine(folder, "watch.txt"), log);
        }

        AutomationElement? search = null;
        if (Locate(handle, window, name, ref search, Tell) is null) Tell($"{name} is not in view: search for it in Tosi Client, and it is watched from then on.");
        Tell($"Watching {name} in Tosi Client for {seconds} s. Each change is saved in {folder}.");

        var began = DateTime.UtcNow;
        string? last = null;
        var saved = 0;
        while (DateTime.UtcNow - began < TimeSpan.FromSeconds(seconds))
        {
            try
            {
                var seen = Look(window);
                var now = Row(seen, name) is { } row && Read(seen, row) is var s
                    ? $"\"{s.Status}\" - {(s.Connected ? "connected" : s.Connecting ? "connecting" : s.Leaving ? "disconnecting" : s.Idle ? "not connected" : "neither")}, as {s.Why}; {(s.Button is { } b ? $"button {b.Name}" : "no button")}"
                    : "(the Lock is not in view)";
                now += $"; adapter: {AdapterSays()}";
                if (now != last)
                {
                    last = now;
                    Tell($"{(DateTime.UtcNow - began).TotalSeconds,6:0.0} s  {now}");
                    if (saved < 60)
                    {
                        saved++;
                        var lines = new List<string>();
                        Write(window, 0, lines.Add);
                        File.WriteAllLines(Path.Combine(folder, $"{saved:00}.txt"), lines);
                        Snap(Path.Combine(folder, $"{saved:00}.png"), handle);
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Changed while it was read: looked at again.
            }
            Thread.Sleep(500);
        }
        ClearSearch(handle, window, Tell);
        Tell("Done watching.");
        return 0;
    }

    // ---------------------------------------------------------------- its adapter

    /// <param name="Up">Windows has it up: a TAP adapter is up only while a tunnel holds it open.</param>
    private sealed record TosiAdapter(string Name, bool Up, string Addresses);

    /// <summary>Tosi Client's network adapter (Tosibox TAP-Windows Adapter): the one up, if one is.</summary>
    private static TosiAdapter? Adapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => Product().IsMatch(n.Description))
                .Select(n => new TosiAdapter(
                    $"{n.Name} ({n.Description})",
                    n.OperationalStatus == OperationalStatus.Up,
                    string.Join(", ", n.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => $"{a.Address}/{a.PrefixLength}"))))
                .OrderByDescending(a => a.Up)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>How the adapter is, for the activity: "up at …", "down", or that there is none.</summary>
    private static string AdapterSays() =>
        Adapter() is { } adapter ? $"{(adapter.Up ? "up" : "down")}{(adapter.Addresses.Length > 0 ? $" at {adapter.Addresses}" : "")}" : "none named for Tosi";

    // ---------------------------------------------------------------- Windows

    [GeneratedRegex(@"^(Tosi|TOSIBOX) Key\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainTitle();

    [GeneratedRegex(@"^(Tosi|TOSIBOX)\b.*password", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordTitle();

    [GeneratedRegex(@"^((Dis)?connect|Cancel)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectButton();

    /// <summary>
    /// Tosi Key's "Access rights changed": New access rights (from …) - "You have been granted access
    /// to the following Gateways" - or Access rights removed (by …) - "Your access to following Gateways
    /// has been revoked" - or still receiving them. Not its wizards for giving access rights to
    /// other keys (Edit access rights, Choose access rights, Confirm access rights).
    /// </summary>
    [GeneratedRegex(@"\baccess rights (changed|removed)\b|\bnew access rights\b|\bnew serializations\b|\bgranted access to the following\b|\bhas been revoked\b|\breceiving access right changes\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccessRights();

    /// <summary>Locks given to the key, to be ticked for it to take them.</summary>
    [GeneratedRegex(@"\bgranted access\b|\bnew access rights\b|\bnew serializations\b", RegexOptions.IgnoreCase)]
    private static partial Regex Granted();

    [GeneratedRegex(@"\breceiving access right changes\b", RegexOptions.IgnoreCase)]
    private static partial Regex Receiving();

    /// <summary>A button that turns down what a dialog offers: never pressed for a person.</summary>
    [GeneratedRegex(@"^(cancel|no|reject|decline|remove|delete|later|abort)$", RegexOptions.IgnoreCase)]
    private static partial Regex Refusing();

    /// <summary>On the way up: connecting, authenticating, assigning an address … or anything else with an ellipsis.</summary>
    [GeneratedRegex(@"\b(re)?connecting\b|\bauthenticat|\bassigning\b|\bnegotiat|\bestablishing\b|\binitiali[sz]|\bhandshak|\bresolving\b|(\.\.\.|…)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectingNow();

    [GeneratedRegex(@"\bdisconnecting\b", RegexOptions.IgnoreCase)]
    private static partial Regex Disconnecting();

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

    /// <summary>A MAC address, as the client's MAC column shows it (10:9a:b9:08:72:2a).</summary>
    [GeneratedRegex(@"^([0-9a-f]{2}[:-]){5}[0-9a-f]{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex Hardware();

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
