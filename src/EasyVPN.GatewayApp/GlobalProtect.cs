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
/// GlobalProtect, Palo Alto Networks' VPN client, has no command line on Windows, so the gateway's
/// globalprotect connector has this app, started in the desktop session where GlobalProtect's
/// agent (PanGPA) runs, do what a person would in its status panel: choose the facility's portal,
/// press Connect, sign in with the facility's user name and password when it asks, and wait until
/// it shows Connected - or press Disconnect, and wait until it shows it is not. Through UI
/// Automation, and with the mouse and keyboard where that does not reach.
///
///   EasyVPN.GatewayApp.exe --globalprotect connect vpn.example.com [--start]   EASYVPN_USER, EASYVPN_PASSWORD
///   EasyVPN.GatewayApp.exe --globalprotect disconnect [--start]
///   EasyVPN.GatewayApp.exe --globalprotect dump > globalprotect.txt       what it sees of GlobalProtect
///
/// With --start, an agent that is not running here is started. The gateway is GlobalProtect's only
/// user: connected to another portal, that is disconnected first. When the panel cannot be read,
/// what was seen of GlobalProtect is saved to <see cref="DumpPath"/>, and said.
/// </summary>
public static partial class GlobalProtect
{
    /// <summary>The exit code for "no GlobalProtect on this desktop": the gateway tries the next session.</summary>
    public const int NotHere = 3;

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    // Signing in to the portal, then to its gateway; the gateway gives the whole connector longer than this.
    private static readonly TimeSpan Connecting = TimeSpan.FromSeconds(150);
    // How often a long wait says it is still waiting, so whoever asked sees something happen.
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    // A portal and its gateway may each ask. Asked once more, the password was refused - and is not
    // tried again and again, which could lock the account.
    private const int SignIns = 2;

    public static string DumpPath { get; } = Path.Combine(Path.GetDirectoryName(CrashLog.Path)!, "globalprotect.txt");

    // When the panel was last brought up, so one that will not come is not asked for again and again;
    // and whether GlobalProtect was started again for it, which is done once at most.
    private static DateTime _broughtUp;
    private static bool _launched;

    public static int Run(string[] args, Action<string> say)
    {
        var user = Environment.GetEnvironmentVariable("EASYVPN_USER");
        var password = Environment.GetEnvironmentVariable("EASYVPN_PASSWORD");
        try
        {
            return args switch
            {
                ["connect", var portal, .. var options] => Connect(portal.Trim(), user?.Trim(), password, options.Contains("--start"), say),
                ["disconnect", .. var options] => Disconnect(options.Contains("--start"), say),
                ["dump"] => Dump(say),
                _ => Usage(say),
            };
        }
        catch (Exception ex)
        {
            // Never a crash: said, and written down whole.
            CrashLog.Write(ex, "driving GlobalProtect");
            say($"GlobalProtect could not be driven ({ex.GetType().Name}: {ex.Message}).{Saved()} The details are in {CrashLog.Path} on the gateway.");
            return 1;
        }
    }

    private static int Usage(Action<string> say)
    {
        say("--globalprotect connect <portal> [--start] | disconnect [--start] | dump");
        return 1;
    }

    // ---------------------------------------------------------------- connect, disconnect

    private static int Connect(string portal, string? user, string? password, bool start, Action<string> say)
    {
        var view = Open(start, say, out var code);
        if (view is null) return code;
        var state = Read(view);
        say($"GlobalProtect shows: {state.Status}.");

        if ((state.Connected || state.Busy || state.SigningIn) && !OnPortal(view, portal))
        {
            // The gateway is GlobalProtect's only user: another portal's connection goes first - and
            // a sign-in left asking for one, which is never given this facility's password.
            var doing = state.Connected ? "connected" : state.SigningIn ? "asking to sign in" : "connecting";
            say($"GlobalProtect is {doing}{(LastPortal() is { } last && !Same(last, portal) ? $" to {last}" : "")}, not to {portal}: ending that first.");
            if (HangUp(view, state, say) != 0) return 1;
            view = Panel();
            state = Read(view);
        }
        else if (state.Connected)
        {
            say($"GlobalProtect is connected to {portal}.");
            return 0;
        }

        if (state.Busy)
            say($"GlobalProtect is connecting to {portal} already.");
        else if (!state.SigningIn)
        {
            if (!ChoosePortal(portal, say)) return 1;
            view = Panel();
            state = Read(view);
            if (state.Connect is not { } button)
            {
                say($"GlobalProtect shows no Connect button. It says: {state.Status}.{Saved()}");
                return 1;
            }
            if (!button.Enabled && !WaitUntil(() => ButtonIn(Panel().Shown, ConnectButton()) is { Enabled: true }, TimeSpan.FromSeconds(3)))
            {
                say($"GlobalProtect keeps its Connect button off. It says: {state.Status}.{Saved()}");
                return 1;
            }
            Push(Panel(), ConnectButton(), say);
            say($"Pressed Connect in GlobalProtect, for {portal}.");
        }

        // Done when it says so. Meanwhile it may ask to sign in, to the portal and then its gateway.
        var signIns = 0;
        var began = DateTime.UtcNow;
        var told = began;
        var lastStatus = state.Status;
        DateTime? idle = null;
        while (DateTime.UtcNow < began + Connecting)
        {
            Thread.Sleep(500);
            if (DateTime.UtcNow - told >= Heartbeat)
            {
                told = DateTime.UtcNow;
                say($"Still waiting for GlobalProtect to connect to {portal} ({(told - began).TotalSeconds:0} s).");
            }
            try
            {
                view = Panel();
                state = Read(view);
                if (state.Connected)
                {
                    say($"GlobalProtect has connected to {portal}.");
                    return 0;
                }
                if (state.Status != lastStatus)
                {
                    lastStatus = state.Status;
                    told = DateTime.UtcNow;
                    say($"GlobalProtect shows: {state.Status}.");
                }

                if (state.SigningIn)
                {
                    if (signIns == SignIns)
                    {
                        say($"GlobalProtect asks to sign in once more: it did not take the user name and password. It says: {state.Status}.{Saved()}");
                        return 1;
                    }
                    if (SignIn(view, portal, user, password) is { } why)
                    {
                        say($"{why}{Saved()}");
                        return 1;
                    }
                    signIns++;
                    say(signIns == 1
                        ? $"Signed in to GlobalProtect{(string.IsNullOrEmpty(user) ? "" : $" as {user}")}."
                        : "GlobalProtect asked to sign in again (its gateway, after its portal), and was signed in again.");
                    // Taken, the sign-in goes; one still there a while later was refused.
                    if (!WaitUntil(() => !Read(Panel()).SigningIn, TimeSpan.FromSeconds(20)))
                    {
                        say($"GlobalProtect still asks to sign in: it did not take the user name and password. It says: {Read(Panel()).Status}.{Saved()}");
                        return 1;
                    }
                    continue;
                }

                // Back where it began - nothing under way, Connect offered again - for a while: it gave up.
                if (!state.Busy && state.Connect is not null)
                {
                    idle ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - idle >= TimeSpan.FromSeconds(10))
                    {
                        say($"GlobalProtect did not connect to {portal}. It says: {state.Status}.{Saved()}");
                        return 1;
                    }
                }
                else idle = null;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Changed while it was read: look again.
            }
        }
        say($"GlobalProtect did not show {portal} connected within {Connecting.TotalSeconds:0} s. It says: {state.Status}. " +
            $"A portal that signs in in a browser, or asks for a code, needs a person: use the manual connector for it.{Saved()}");
        return 1;
    }

    private static int Disconnect(bool start, Action<string> say)
    {
        var view = Open(start, say, out var code);
        if (view is null) return code;
        var state = Read(view);
        if (!state.Connected && !state.Busy && !state.SigningIn)
        {
            say($"GlobalProtect is not connected. It says: {state.Status}.");
            return 0;
        }
        say($"GlobalProtect shows: {state.Status}.");
        return HangUp(view, state, say);
    }

    /// <summary>Presses Disconnect - or Cancel, for a connection still being made - and waits until GlobalProtect shows it ended.</summary>
    private static int HangUp(View view, State state, Action<string> say)
    {
        var button = state.Connected ? state.Disconnect : state.Cancel ?? state.Disconnect;
        if (button is null)
        {
            say(state.Connected
                ? $"GlobalProtect shows no Disconnect, so it cannot be disconnected from here: its portal must let users disconnect (the On-demand connect method). It says: {state.Status}.{Saved()}"
                : $"GlobalProtect is still connecting, and shows no Cancel to stop it. It says: {state.Status}.{Saved()}");
            return 1;
        }
        var name = button.Name.Trim();
        if (!button.Enabled)
        {
            say($"GlobalProtect keeps its {name} button off. It says: {state.Status}.{Saved()}");
            return 1;
        }
        Push(view, Named(name), say);
        say($"Pressed {name} in GlobalProtect.");

        var began = DateTime.UtcNow;
        var told = began;
        while (DateTime.UtcNow < began + Settle)
        {
            Thread.Sleep(500);
            if (DateTime.UtcNow - told >= Heartbeat)
            {
                told = DateTime.UtcNow;
                say($"Still waiting for GlobalProtect to disconnect ({(told - began).TotalSeconds:0} s).");
            }
            try
            {
                var now = Panel();
                if (!Recognized(now)) continue;
                state = Read(now);
                if (!state.Connected && !state.Busy && !state.SigningIn)
                {
                    say($"GlobalProtect has disconnected. It says: {state.Status}.");
                    return 0;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Changed while it was read: look again.
            }
        }
        // Asked why, or for a passcode, before it lets go: that is for a person.
        say($"GlobalProtect did not show itself disconnected within {Settle.TotalSeconds:0} s. It says: {state.Status}.{Saved()}");
        return 1;
    }

    // ---------------------------------------------------------------- the status panel

    /// <summary>What the agent shows: each of its windows in view, with what is in it.</summary>
    private sealed record View(List<(IntPtr Window, List<Seen> Seen)> Windows)
    {
        public List<Seen> Shown { get; } = [.. Windows.SelectMany(w => w.Seen).Where(s => s.Visible)];

        public IntPtr WindowOf(Seen seen) => Windows.First(w => w.Seen.Contains(seen)).Window;
    }

    /// <param name="Busy">Connecting or disconnecting: neither yet.</param>
    /// <param name="SigningIn">It asks for a password.</param>
    /// <param name="Status">What it says, for the activity log.</param>
    private sealed record State(bool Connected, bool Busy, bool SigningIn, Seen? Connect, Seen? Disconnect, Seen? Cancel, string Status);

    /// <summary>
    /// What the panel shows. Connected is a Disconnect button, or a status of Connected; it asks to
    /// sign in when it shows a password box; it is busy when it says it is connecting (or
    /// authenticating, or still working …). Not connected, whatever else it shows, when it says so.
    /// </summary>
    private static State Read(View view)
    {
        var shown = view.Shown;
        // "Connecting...", said as "Connecting".
        var texts = shown.Where(s => !NotText.Contains(s.Type)).Select(s => s.Name.Trim().TrimEnd('.', '…')).Where(t => t.Length > 0).Distinct().ToList();
        var signingIn = shown.Any(s => s.Type == ControlType.Edit && s.Password);
        var busy = !signingIn && texts.Any(t => Busy().IsMatch(t));
        var idle = texts.Any(t => NotConnected().IsMatch(t));
        var disconnect = ButtonIn(shown, DisconnectButton());
        var connected = !signingIn && !busy && !idle && (disconnect is not null || texts.Any(t => Connected().IsMatch(t)));
        var status = texts.Count == 0 ? "(nothing UI Automation can read)"
            : string.Join(" | ", texts.Take(8).Select(t => t.Length > 80 ? t[..80] + "…" : t));
        return new State(connected, busy, signingIn, ButtonIn(shown, ConnectButton()), disconnect, ButtonIn(shown, CancelButton()), status);
    }

    // What says nothing of the state: buttons and boxes are looked at for what they are.
    private static readonly HashSet<ControlType> NotText =
    [
        ControlType.Button, ControlType.Edit, ControlType.ComboBox, ControlType.List, ControlType.ListItem, ControlType.Window,
        ControlType.TitleBar, ControlType.MenuBar, ControlType.Menu, ControlType.MenuItem, ControlType.ScrollBar, ControlType.Thumb,
    ];

    /// <summary>Whether it shows anything of its status panel this knows.</summary>
    private static bool Recognized(View view) =>
        view.Shown.Any(s => s.Type == ControlType.Edit && s.Password)
        || ButtonIn(view.Shown, ConnectButton()) is not null || ButtonIn(view.Shown, DisconnectButton()) is not null
        || ButtonIn(view.Shown, GetStarted()) is not null
        || view.Shown.Any(s => !NotText.Contains(s.Type) && (Connected().IsMatch(s.Name.Trim()) || NotConnected().IsMatch(s.Name.Trim()) || Busy().IsMatch(s.Name.Trim())));

    /// <summary>A button so named: a button, or else whatever else is, but a box - a panel that draws its own may call it otherwise.</summary>
    private static Seen? ButtonIn(List<Seen> shown, Regex name) =>
        shown.Where(s => s.Type != ControlType.Edit && name.IsMatch(s.Name.Trim()))
            .OrderByDescending(s => s.Type == ControlType.Button || s.Type == ControlType.Hyperlink || s.Type == ControlType.SplitButton)
            .FirstOrDefault();

    /// <summary>
    /// GlobalProtect's status panel, in view: started first when it is not running (with --start),
    /// brought up when it is out of sight, and past its welcome page.
    /// </summary>
    /// <returns>What it shows; null when it is not running here, or could not be brought up (said, with the exit code).</returns>
    private static View? Open(bool start, Action<string> say, out int code)
    {
        code = NotHere;
        if (Agents().Count == 0)
        {
            if (!start)
            {
                say("GlobalProtect is not running on this desktop.");
                return null;
            }
            code = 1;
            if (!Start(say)) return null;
        }

        code = 1;
        var view = See(Agents());
        if (!Recognized(view))
        {
            BringUp();
            // Just started, it takes a while to reach its service and lay out its panel.
            if (!WaitUntil(() => Recognized(view = See(Agents())), TimeSpan.FromSeconds(15)))
            {
                say($"GlobalProtect runs here, but its status panel could not be brought up, or shows nothing this knows.{Saved()}");
                return null;
            }
        }

        // The first time, its welcome page.
        if (ButtonIn(view.Shown, GetStarted()) is not null)
        {
            Push(view, GetStarted(), say);
            say("Pressed Get Started on GlobalProtect's welcome page.");
            WaitUntil(() => ButtonIn((view = Panel()).Shown, GetStarted()) is null, TimeSpan.FromSeconds(5));
        }
        return view;
    }

    /// <summary>What the agent shows now: its status panel, brought up again should it have gone out of sight.</summary>
    private static View Panel()
    {
        var view = See(Agents());
        if (Recognized(view) || DateTime.UtcNow - _broughtUp < TimeSpan.FromSeconds(10)) return view;
        BringUp();
        return See(Agents());
    }

    /// <summary>
    /// The status panel brought up as a person would: with its icon in the notification area, or
    /// else from the Start menu - its program, started again, shows the one running. That is done
    /// once at most, should it start another copy instead.
    /// </summary>
    private static void BringUp()
    {
        _broughtUp = DateTime.UtcNow;
        if (Icon() is (var tray, var icon))
        {
            try
            {
                Press(tray, icon);
                if (WaitUntil(() => Recognized(See(Agents())), TimeSpan.FromSeconds(5))) return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                // Not to be pressed now: the Start menu's way, then.
            }
        }
        if (!_launched && Program() is { } program)
        {
            _launched = true;
            Launch(program, null);
        }
    }

    /// <summary>The agent's processes in this desktop session.</summary>
    private static HashSet<uint> Agents()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var agents = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("PanGPA"))
        {
            using (process)
                if (process.SessionId == session) agents.Add((uint)process.Id);
        }
        return agents;
    }

    /// <summary>The agent's top-level windows: those in view, or all of them.</summary>
    private static List<IntPtr> WindowsOf(HashSet<uint> agents, bool shown = true)
    {
        var found = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var process);
            if (agents.Contains(process) && (!shown || IsWindowVisible(handle))) found.Add(handle);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Every window of the agent in view, and what is in each: its status panel, and its sign-in should that be a window of its own.</summary>
    private static View See(HashSet<uint> agents)
    {
        var windows = new List<(IntPtr, List<Seen>)>();
        foreach (var handle in WindowsOf(agents))
        {
            try
            {
                windows.Add((handle, Look(AutomationElement.FromHandle(handle))));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
            {
                // Gone meanwhile.
            }
        }
        return new View(windows);
    }

    /// <summary>
    /// Presses the panel's button so named through UI Automation, and, when the panel still shows it
    /// as it was a few seconds later, clicks it once as a person would: a panel may take UI
    /// Automation's press and do nothing. Never more - a second press that did take could undo the first.
    /// </summary>
    private static void Push(View view, Regex name, Action<string> say)
    {
        if (ButtonIn(view.Shown, name) is not { } button) return;
        if (!button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            Click(view.WindowOf(button), Middle(button));
            return;
        }
        var before = Read(view).Status;
        ((InvokePattern)invoke).Invoke();

        View? now = null;
        bool Unchanged() => ButtonIn((now = Panel()).Shown, name) is not null && Read(now).Status == before;
        if (WaitUntil(() => !Unchanged(), TimeSpan.FromSeconds(5)) || now is null || ButtonIn(now.Shown, name) is not { } still) return;
        try
        {
            Click(now.WindowOf(still), Middle(still));
            say($"GlobalProtect did not take the press of {still.Name.Trim()}, so it was clicked with the mouse.");
        }
        catch (InvalidOperationException ex)
        {
            say(ex.Message);
        }
    }

    private static Point Middle(Seen s) => new(s.Rect.Left + s.Rect.Width / 2, s.Rect.Top + s.Rect.Height / 2);

    private static Regex Named(string name) => new($"^{Regex.Escape(name)}$", RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- the portal

    /// <summary>
    /// The facility's portal chosen in the panel, as a person would: typed into its portal box (the
    /// first time GlobalProtect is used, or a drop-down that takes typing), or picked from its
    /// Change Portal drop-down. One the panel shows already is left as it is.
    /// </summary>
    /// <returns>False when it could not be chosen (said).</returns>
    private static bool ChoosePortal(string portal, Action<string> say)
    {
        var view = Panel();
        var shown = view.Shown;

        if (shown.FirstOrDefault(s => s.Type == ControlType.Edit && !s.Password && !s.ReadOnly && s.Enabled) is { } box)
        {
            if (Same(box.Value, portal)) return true;
            var entered = Enter(view.WindowOf(box), box.Element, portal);
            if (entered is null)
            {
                say($"GlobalProtect's portal box could not be typed into or filled.{Saved()}");
                return false;
            }
            say($"Entered the portal {portal} in GlobalProtect{(entered == true ? "" : " (put in its box, not typed)")}.");
            return true;
        }

        var choice = shown.FirstOrDefault(s => s.Type == ControlType.ComboBox)
            ?? shown.FirstOrDefault(s => Openers.Contains(s.Type) && PortalChoice().IsMatch(s.Name.Trim()));
        if (choice is not null)
        {
            if (Same(Chosen(choice.Element), portal)) return true;
            if (Pick(view, choice, portal, out var offered))
            {
                say($"Chose the portal {portal} in GlobalProtect.");
                return true;
            }
            say($"GlobalProtect does not offer the portal {portal} to choose{(offered.Count > 0 ? $" (it offers {string.Join(", ", offered)})" : "")}. " +
                $"Connect to it by hand once on the gateway's desktop, so GlobalProtect has it.{Saved()}");
            return false;
        }

        if (OnPortal(view, portal)) return true;
        say($"GlobalProtect does not show the portal {portal}, and has no portal box or drop-down to choose it with{(LastPortal() is { } last ? $"; its portal is {last}" : "")}. " +
            $"Connect to it by hand once on the gateway's desktop, so GlobalProtect has it.{Saved()}");
        return false;
    }

    // What opens a drop-down a panel draws itself, and what the list it opens shows its entries as.
    private static readonly HashSet<ControlType> Openers = [ControlType.Button, ControlType.SplitButton, ControlType.MenuItem, ControlType.Hyperlink];
    private static readonly HashSet<ControlType> Entries = [ControlType.ListItem, ControlType.MenuItem, ControlType.DataItem, ControlType.Button, ControlType.Text];

    /// <summary>The portal picked from the drop-down: opened, its entry chosen, closed again.</summary>
    /// <param name="offered">What the drop-down offered, when the portal was not among it.</param>
    private static bool Pick(View view, Seen choice, string portal, out List<string> offered)
    {
        offered = [];
        var window = view.WindowOf(choice);
        var expand = choice.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) ? (ExpandCollapsePattern)pattern : null;
        try
        {
            if (expand is not null) expand.Expand();
            else Press(window, choice.Element);
            Thread.Sleep(500);

            // Its entries: in the drop-down itself, or in a list it opened, a window of its own.
            var entries = choice.Element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>()
                .Select(e => (Window: window, Element: e, Name: e.Current.Name.Trim()))
                .ToList();
            if (entries.Count == 0)
            {
                var opened = Panel();
                entries = [.. opened.Shown.Where(s => s.Element != choice.Element && Entries.Contains(s.Type))
                    .Select(s => (Window: opened.WindowOf(s), s.Element, Name: s.Name.Trim()))];
            }

            if (entries.FirstOrDefault(e => Same(e.Name, portal)) is not { Element: not null } entry)
            {
                offered = [.. entries.Select(e => e.Name).Where(n => n.Length > 0 && n.Contains('.')).Distinct()];
                return false;
            }
            if (entry.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
            else Press(entry.Window, entry.Element);
            return WaitUntil(() => Same(Chosen(choice.Element), portal) || OnPortal(Panel(), portal), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (expand?.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                // Closed by itself.
            }
        }
    }

    /// <summary>What a drop-down shows chosen: its value, its selected entry, or else its name.</summary>
    private static string Chosen(AutomationElement choice)
    {
        try
        {
            if (choice.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.Value is { Length: > 0 } text) return text;
            if (choice.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection) && ((SelectionPattern)selection).Current.GetSelection() is [var entry, ..])
                return entry.Current.Name;
            return choice.Current.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return "";
        }
    }

    /// <summary>Whether GlobalProtect is on the portal: shown in its panel, or the one it last connected to.</summary>
    private static bool OnPortal(View view, string portal) =>
        view.Shown.Any(s => Mentions(s.Name, portal) || Mentions(s.Value, portal)) || Mentions(LastPortal() ?? "", portal);

    /// <summary>Whether the text names the portal: alone, or among other words ("Portal: vpn.example.com").</summary>
    private static bool Mentions(string text, string portal) =>
        Bare(portal) is { Length: > 0 } bare && Regex.IsMatch(Bare(text), $@"(^|[\s:(\[]){Regex.Escape(bare)}($|[\s):\]])", RegexOptions.IgnoreCase);

    private static bool Same(string a, string b) => string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A portal's address without https:// or a / after it, as GlobalProtect may show it either way.</summary>
    private static string Bare(string address) => Scheme().Replace(address.Trim(), "").TrimEnd('/');

    /// <summary>The portal GlobalProtect last connected to, as it keeps it for this user.</summary>
    private static string? LastPortal()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Palo Alto Networks\GlobalProtect\Settings");
            return settings?.GetValue("LastUrl") is string { Length: > 0 } last ? last.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- signing in

    /// <summary>
    /// GlobalProtect's sign-in, filled in with the facility's user name and password, and sent. Its
    /// boxes are told apart by what they are, not by what they are called, as GlobalProtect may call
    /// them wrongly: the password's hides what it holds, and the user name's is the box above it.
    /// </summary>
    /// <returns>Null when sent; otherwise why it could not be.</returns>
    private static string? SignIn(View view, string portal, string? user, string? password)
    {
        var shown = view.Shown;
        if (shown.FirstOrDefault(s => s.Type == ControlType.Edit && s.Password) is not { } secret) return "GlobalProtect's sign-in shows no password box.";
        if (string.IsNullOrEmpty(password))
            return $"GlobalProtect asks to sign in to {portal}: set the facility's user and password in the facility editor.";

        var window = view.WindowOf(secret);
        var name = shown
            .Where(s => s.Type == ControlType.Edit && !s.Password && !s.ReadOnly && s.Rect.Bottom <= secret.Rect.Top + 4 && view.WindowOf(s) == window)
            .MaxBy(s => s.Rect.Top);
        if (name is not null && !string.IsNullOrEmpty(user) && !string.Equals(name.Value.Trim(), user, StringComparison.OrdinalIgnoreCase))
        {
            if (Enter(window, name.Element, user) is null) return "GlobalProtect's user name box could not be typed into or filled.";
        }
        else if (name is not null && string.IsNullOrEmpty(user) && name.Value.Trim().Length == 0)
            return $"GlobalProtect asks for a user name for {portal}: set the facility's user in the facility editor.";
        if (Enter(window, secret.Element, password) is null) return "GlobalProtect's password box could not be typed into or filled.";

        // Its Sign In button, below the boxes, once it takes it; or else Enter, as a person would.
        var below = Panel().Shown.Where(s => s.Rect.Top >= secret.Rect.Top).ToList();
        if ((ButtonIn(below, SignInButton()) ?? ButtonIn(below, SendButton())) is { } send)
        {
            WaitUntil(() => send.Element.Current.IsEnabled, TimeSpan.FromSeconds(3));
            Press(window, send.Element);
        }
        else
        {
            Front(window);
            secret.Element.SetFocus();
            PressKey(0x0D); // Enter
        }
        return null;
    }

    // ---------------------------------------------------------------- starting it

    /// <summary>Starts the agent, and waits for it to run.</summary>
    private static bool Start(Action<string> say)
    {
        if (Program() is not { } program)
        {
            say("GlobalProtect is not running, and its program (PanGPA.exe) was not found to start it: is GlobalProtect installed on this gateway?");
            return false;
        }
        say($"Starting GlobalProtect ({program}).");
        if (!Launch(program, say)) return false;
        if (!WaitUntil(() => Agents().Count > 0, TimeSpan.FromSeconds(30)))
        {
            say($"{program} was started, but GlobalProtect did not run within 30 s.");
            return false;
        }
        return true;
    }

    private static bool Launch(string program, Action<string>? say)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(program) })?.Dispose();
            return true;
        }
        catch (Win32Exception ex)
        {
            say?.Invoke($"GlobalProtect could not be started: {ex.Message}");
            return false;
        }
    }

    /// <summary>GlobalProtect's agent, PanGPA.exe: where it runs, or where its installer put it.</summary>
    private static string? Program()
    {
        foreach (var process in Process.GetProcessesByName("PanGPA"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } running && File.Exists(running)) return running;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Not ours to look at.
                }
            }
        }

        var folders = new List<string?> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Palo Alto Networks", "GlobalProtect") };
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var programs = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            foreach (var key in programs?.GetSubKeyNames() ?? [])
            {
                using var entry = programs!.OpenSubKey(key);
                if (entry?.GetValue("DisplayName") is string name && name.StartsWith("GlobalProtect", StringComparison.OrdinalIgnoreCase))
                    folders.Add(entry.GetValue("InstallLocation") as string);
            }
        }
        return folders.OfType<string>().Where(f => f.Length > 0).Select(f => Path.Combine(f, "PanGPA.exe")).FirstOrDefault(File.Exists);
    }

    /// <summary>Its icon in the notification area: on the taskbar, or among the icons it keeps out of sight.</summary>
    private static (IntPtr Tray, AutomationElement Icon)? Icon()
    {
        foreach (var tray in new[] { "Shell_TrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
        {
            var handle = FindTopWindow(tray, null);
            if (handle == IntPtr.Zero) continue;
            try
            {
                var icon = AutomationElement.FromHandle(handle)
                    .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(b => b.Current.Name.Contains("GlobalProtect", StringComparison.OrdinalIgnoreCase));
                if (icon is not null) return (handle, icon);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Changing meanwhile: the next.
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- what it sees, written down

    private static int Dump(Action<string> say)
    {
        var agents = Agents();
        say($"GlobalProtect's agent (PanGPA): {(agents.Count == 0 ? "not running on this desktop" : $"process {string.Join(", ", agents)}")}; " +
            $"its program: {Program() ?? "not found"}; the portal it last connected to: {LastPortal() ?? "none"}; " +
            $"its icon in the notification area: {(Icon() is (_, var icon) ?$"\"{icon.Current.Name}\"" : "not found")}.");
        foreach (var handle in WindowsOf(agents, shown: false))
        {
            var title = new StringBuilder(256);
            GetWindowText(handle, title, title.Capacity);
            if (!IsWindowVisible(handle))
            {
                if (title.Length > 0) say($"Window \"{title}\", hidden.");
                continue;
            }
            say($"Window \"{title}\":");
            Write(AutomationElement.FromHandle(handle), 1, say);
        }
        return agents.Count == 0 ? NotHere : 0;
    }

    /// <summary>What was seen of GlobalProtect, saved for working out why: " It is in …", or nothing when it could not be.</summary>
    private static string Saved()
    {
        try
        {
            var lines = new List<string>();
            Dump(lines.Add);
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

    [GeneratedRegex(@"^connect$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectButton();

    [GeneratedRegex(@"^disconnect$", RegexOptions.IgnoreCase)]
    private static partial Regex DisconnectButton();

    [GeneratedRegex(@"^cancel$", RegexOptions.IgnoreCase)]
    private static partial Regex CancelButton();

    [GeneratedRegex(@"^(sign|log) ?in$", RegexOptions.IgnoreCase)]
    private static partial Regex SignInButton();

    /// <summary>What else sends a sign-in: Connect, as GlobalProtect calls it when it signs in again.</summary>
    [GeneratedRegex(@"^(connect|ok|submit|continue)$", RegexOptions.IgnoreCase)]
    private static partial Regex SendButton();

    [GeneratedRegex(@"^get started$", RegexOptions.IgnoreCase)]
    private static partial Regex GetStarted();

    /// <summary>Change Portal, or a drop-down called Portal.</summary>
    [GeneratedRegex(@"\bportal\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortalChoice();

    [GeneratedRegex(@"^(you are |globalprotect is )?connected\b", RegexOptions.IgnoreCase)]
    private static partial Regex Connected();

    [GeneratedRegex(@"\bnot connected\b|^disconnected\b|\bconnection failed\b|^disabled\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotConnected();

    /// <summary>Under way: connecting, disconnecting, and what it says meanwhile.</summary>
    [GeneratedRegex(@"\b(re)?connecting\b|\bdisconnecting\b|still working|authenticating|retrieving|discovering|signing in|please wait|loading", RegexOptions.IgnoreCase)]
    private static partial Regex Busy();

    [GeneratedRegex(@"https?://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindTopWindow(string className, string? title);
}
