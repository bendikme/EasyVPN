<#
    Drives eCatcher (Talk2M) on this desktop for the ecatcher connector, as a person would: it
    starts eCatcher if need be, signs in to Talk2M, searches for the Ewon, selects it and presses
    Connect or Disconnect, and waits until eCatcher shows it done. eCatcher is a Java program with
    no command line; it is reached through Java's own accessibility bridge, which comes with the
    Java eCatcher ships with - 32-bit, so this runs in 32-bit PowerShell. The gateway starts it in
    the desktop session someone is at, with that user's full rights (eCatcher runs as administrator):

        ecatcher-ui.ps1 Connect [-Start] | Disconnect | Dump

    EASYVPN_EWON        the Ewon, as eCatcher lists it
    EASYVPN_ACCOUNT     the Talk2M account
    EASYVPN_USER        the Talk2M user
    EASYVPN_PASSWORD    its password
    EASYVPN_PROGRAM     eCatcher.exe, when it is not where its installer put it

    The bridge is switched on for the signed-in user (jabswitch -enable) the first time; an eCatcher
    already running without it is started again. Exit code 0 is done, 1 failed (and said why), 3
    eCatcher is not running on this desktop (without -Start). When it cannot go on, what it saw of
    eCatcher is saved to %LOCALAPPDATA%\EasyVPN\ecatcher.txt, and said.
#>
param([ValidateSet('Connect', 'Disconnect', 'Dump')][string]$Action, [switch]$Start)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$notHere = 3
$dumpPath = Join-Path $env:LOCALAPPDATA 'EasyVPN\ecatcher.txt'
$script:frame = $null

# Said at once, never taken for what a function returns.
function Say([string]$text) { [Console]::Out.WriteLine($text) }

trap {
    Say "eCatcher could not be driven: $($_.Exception.Message)$(Save-Dump)"
    exit 1
}

# ---------------------------------------------------------------- eCatcher, and its Java

function Find-Program {
    if ($env:EASYVPN_PROGRAM) {
        if (Test-Path $env:EASYVPN_PROGRAM) { return $env:EASYVPN_PROGRAM }
        throw "eCatcher is not at $env:EASYVPN_PROGRAM, where its program is set."
    }
    $installed = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'eCatcher*' }
    foreach ($app in $installed) {
        foreach ($place in $app.InstallLocation, ("$($app.DisplayIcon)".Trim('"') -replace ',\d+$')) {
            if (-not $place) { continue }
            if ($place -like '*.exe') { $place = Split-Path $place }
            $exe = Join-Path $place 'eCatcher.exe'
            if (Test-Path $exe) { return $exe }
        }
    }
    foreach ($root in ${env:ProgramFiles(x86)}, $env:ProgramFiles) {
        if (-not $root) { continue }
        $exe = Join-Path $root 'eCatcher-Talk2M\eCatcher.exe'
        if (Test-Path $exe) { return $exe }
    }
    throw 'eCatcher is not installed on this gateway. Install it, or set its program in the gateway app''s Settings.'
}

$program = Find-Program
$folder = Split-Path $program
$jre = Join-Path $folder 'jre\bin'
if (-not (Test-Path (Join-Path $jre 'windowsaccessbridge-32.dll'))) {
    throw "eCatcher's Java has no accessibility bridge ($jre\windowsaccessbridge-32.dll)."
}

# Java loads the bridge as it starts, if the user has it switched on.
$properties = Join-Path $env:USERPROFILE '.accessibility.properties'
if (-not ((Test-Path $properties) -and ((Get-Content $properties -Raw) -match '(?m)^\s*assistive_technologies\s*=.*AccessBridge'))) {
    & (Join-Path $jre 'jabswitch.exe') -enable | Out-Null
    Say "Switched Java's accessibility bridge on for $env:USERNAME, so eCatcher can be driven."
}

$env:PATH = "$jre;$env:PATH"
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class JavaNode
{
    public int Vm;
    public long Context;
    public string Role, Name, Description, States;
    public int X, Y, Width, Height, Index, Depth, Children;
    public bool Actionable, HasText;
    public JavaNode Parent;

    public bool Has(string state)
    {
        foreach (var s in States.Split(',')) if (s.Trim() == state) return true;
        return false;
    }

    public override string ToString()
    {
        return string.Format("{0}{1} \"{2}\"{3} [{4}] at {5},{6} {7}x{8}", new string(' ', Depth * 2), Role, Name,
            Description.Length > 0 ? " (" + Description + ")" : "", States, X, Y, Width, Height);
    }
}

public static class JavaBridge
{
    private const string Dll = "windowsaccessbridge-32.dll";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ContextInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string role;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string role_en_US;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string states;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string states_en_US;
        public int indexInParent, childrenCount, x, y, width, height;
        public int accessibleComponent, accessibleAction, accessibleSelection, accessibleText, accessibleInterfaces;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActionInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActionList
    {
        public int count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ActionInfo[] actions;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActionsToDo
    {
        public int count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public ActionInfo[] actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextInfo
    {
        public int charCount, caretIndex, indexAtPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void Windows_run();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int isJavaWindow(IntPtr hwnd);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int getAccessibleContextFromHWND(IntPtr hwnd, out int vm, out long context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int getAccessibleContextInfo(int vm, long context, out ContextInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern long getAccessibleChildFromContext(int vm, long context, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void releaseJavaObject(int vm, long item);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int setTextContents(int vm, long context, string text);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int getAccessibleActions(int vm, long context, ref ActionList actions);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int doAccessibleActions(int vm, long context, ref ActionsToDo actions, out int failure);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int getAccessibleTextInfo(int vm, long context, out TextInfo info, int x, int y);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int getAccessibleTextRange(int vm, long context, int start, int end, StringBuilder text, short length);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void addAccessibleSelectionFromContext(int vm, long context, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void clearAccessibleSelectionFromContext(int vm, long context);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);

    // Each read holds on to what it reached in the Java program; the oldest reads are let go.
    private static readonly Queue<List<JavaNode>> Reads = new Queue<List<JavaNode>>();

    /// <summary>Starts the bridge. The Java programs answer it with window messages, so it waits on them a while.</summary>
    public static void Start()
    {
        Windows_run();
        Pump(1500);
    }

    /// <summary>Lets the bridge's window messages in for a while: it hears of Java programs, and from them, that way.</summary>
    public static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Msg message;
            while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            Thread.Sleep(20);
        } while (DateTime.UtcNow < until);
    }

    /// <summary>The Java windows of the given processes, shown or not, as handle and title.</summary>
    public static List<KeyValuePair<IntPtr, string>> Windows(int[] processes)
    {
        var found = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows(delegate (IntPtr hwnd, IntPtr parameter)
        {
            int process;
            GetWindowThreadProcessId(hwnd, out process);
            if (Array.IndexOf(processes, process) < 0 || isJavaWindow(hwnd) == 0) return true;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            found.Add(new KeyValuePair<IntPtr, string>(hwnd, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool Visible(IntPtr hwnd) { return IsWindowVisible(hwnd); }

    /// <summary>
    /// What a window shows: its tree, without what is hidden (eCatcher keeps every view it has, and
    /// shows one at a time) - but with every row of a list or table, in view or scrolled out of it.
    /// Null when the bridge cannot reach the window.
    /// </summary>
    public static List<JavaNode> Read(IntPtr hwnd)
    {
        int vm;
        long context;
        if (getAccessibleContextFromHWND(hwnd, out vm, out context) == 0) return null;
        var nodes = new List<JavaNode>();
        Walk(vm, context, null, 0, nodes);
        Reads.Enqueue(nodes);
        while (Reads.Count > 4)
            foreach (var node in Reads.Dequeue())
                if (node.Parent != null) releaseJavaObject(node.Vm, node.Context);
        return nodes;
    }

    private static void Walk(int vm, long context, JavaNode parent, int depth, List<JavaNode> nodes)
    {
        ContextInfo info;
        if (getAccessibleContextInfo(vm, context, out info) == 0)
        {
            if (parent != null) releaseJavaObject(vm, context);
            return;
        }
        var node = new JavaNode
        {
            Vm = vm, Context = context, Parent = parent, Depth = depth,
            Role = info.role_en_US ?? "", Name = (info.name ?? "").Trim(), Description = (info.description ?? "").Trim(),
            States = info.states_en_US ?? "", X = info.x, Y = info.y, Width = info.width, Height = info.height,
            Index = info.indexInParent, Children = info.childrenCount,
            Actionable = info.accessibleAction != 0, HasText = info.accessibleText != 0,
        };
        var row = parent != null && (parent.Role == "table" || parent.Role == "list");
        if (parent != null && !row && !node.Has("showing"))
        {
            releaseJavaObject(vm, context);
            return;
        }
        nodes.Add(node);
        if (row) return;
        var count = Math.Min(info.childrenCount, 5000);
        for (var i = 0; i < count; i++)
        {
            var child = getAccessibleChildFromContext(vm, context, i);
            if (child != 0) Walk(vm, child, node, depth + 1, nodes);
        }
    }

    /// <summary>Does what the element does when used: a button's press, a check box's tick.</summary>
    public static bool Act(JavaNode node)
    {
        var list = new ActionList { actions = new ActionInfo[256] };
        if (getAccessibleActions(node.Vm, node.Context, ref list) == 0 || list.count == 0) return false;
        var todo = new ActionsToDo { count = 1, actions = new ActionInfo[32] };
        todo.actions[0].name = list.actions[0].name;
        int failure;
        return doAccessibleActions(node.Vm, node.Context, ref todo, out failure) != 0;
    }

    public static bool SetText(JavaNode node, string text)
    {
        return setTextContents(node.Vm, node.Context, text ?? "") != 0;
    }

    public static string Text(JavaNode node)
    {
        TextInfo info;
        if (getAccessibleTextInfo(node.Vm, node.Context, out info, 0, 0) == 0 || info.charCount <= 0) return "";
        var length = Math.Min(info.charCount, 4000);
        var text = new StringBuilder(length + 1);
        return getAccessibleTextRange(node.Vm, node.Context, 0, length - 1, text, (short)(length + 1)) != 0 ? text.ToString() : "";
    }

    /// <summary>Selects one child of a list or table - a table's cell selects its row - and only that one.</summary>
    public static void Select(JavaNode container, int index)
    {
        clearAccessibleSelectionFromContext(container.Vm, container.Context);
        addAccessibleSelectionFromContext(container.Vm, container.Context, index);
    }
}
'@

# ---------------------------------------------------------------- finding it on this desktop

$session = (Get-Process -Id $PID).SessionId

function Get-Ecatcher([switch]$Elsewhere) {
    @(Get-Process javaw, java, eCatcher -ErrorAction SilentlyContinue | Where-Object {
            ($_.SessionId -eq $session) -ne [bool]$Elsewhere -and "$($_.Path)" -like "$folder\*" })
}

# The main window, or $null: the bridge reaches it only when eCatcher started with the bridge on.
function Find-Frame {
    $ids = [int[]]@(Get-Ecatcher | ForEach-Object Id)
    if (-not $ids) { return $null }
    foreach ($window in [JavaBridge]::Windows($ids)) {
        if ($window.Value -like 'eCatcher*' -and [JavaBridge]::Read($window.Key)) { return $window.Key }
    }
    $null
}

function Start-Ecatcher {
    # One left on a desktop nobody is at would keep this one from signing in.
    foreach ($process in Get-Ecatcher -Elsewhere) {
        $process.Kill()
        Say "Closed eCatcher on another desktop (session $($process.SessionId)), where nobody could use it."
    }
    Say "Starting eCatcher ($program)."
    Start-Process $program -WorkingDirectory $folder
    $until = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $until) {
        [JavaBridge]::Pump(1000)
        $found = Find-Frame
        if ($found) {
            # Its views are filled in a moment after the window shows.
            [JavaBridge]::Pump(3000)
            return $found
        }
    }
    throw 'eCatcher was started, but its window did not come within 90 s.'
}

function Stop-Ecatcher {
    foreach ($process in Get-Ecatcher) { try { $process.Kill() } catch { } }
    Start-Sleep -Seconds 3
}

# ---------------------------------------------------------------- reading it

function Read-Frame { [JavaBridge]::Read($script:frame) }

function Find-Node($nodes, [string]$role, [string]$name) {
    @($nodes | Where-Object { $_.Role -eq $role -and $_.Name -eq $name -and $_.Has('showing') })[0]
}

function Find-Like($nodes, [string]$role, [string]$pattern) {
    @($nodes | Where-Object { $_.Role -eq $role -and $_.Name -match $pattern -and $_.Has('showing') })[0]
}

# What it shows: login, main, code (a two-factor code), elsewhere (signed in on another
# computer), password (to be changed), or unknown.
function Get-View($nodes) {
    if (Find-Like $nodes 'label' 'already connected to Talk2m from another computer') { return 'elsewhere' }
    if (Find-Like $nodes 'label' 'security code|mobile phone number') { return 'code' }
    if (Find-Like $nodes 'label' 'password (must be changed|expired)') { return 'password' }
    if ((Find-Node $nodes 'push button' 'Log In') -and (Find-Node $nodes 'label' 'Account:')) { return 'login' }
    if (Find-Node $nodes 'push button' 'Refresh') { return 'main' }
    'unknown'
}

function Assert-View([string]$view) {
    switch ($view) {
        'code' { throw 'Talk2M asks for a security code (two-factor sign-in). Sign in to eCatcher once by hand on the gateway, ticking "Remember this PC".' }
        'elsewhere' { throw "Talk2M says $env:EASYVPN_USER is signed in on another computer. Give the gateway a Talk2M user of its own, or sign out there." }
        'password' { throw "Talk2M wants a new password for $env:EASYVPN_USER. Change it in eCatcher, then in the facility." }
    }
}

# The button that follows the Ewon selected: Connect, Disconnect, Wake up, or No action.
function Find-ActionButton($nodes) {
    @($nodes | Where-Object { $_.Role -eq 'push button' -and $_.Has('showing') -and $_.Name -match '^(Connect|Disconnect|Wake up|No action)$' })[0]
}

# Connected, as the active connection shows it: its Disconnect, and nothing still in progress.
function Test-Connected($nodes) {
    (Find-Node $nodes 'label' 'Disconnect') -and -not (Find-Node $nodes 'label' 'No active connection.') -and -not (Find-Node $nodes 'push button' 'Cancel')
}

function Labels($nodes) {
    (@($nodes | Where-Object { $_.Role -eq 'label' -and $_.Name -and $_.Has('showing') -and $_.Parent.Role -notin 'table', 'list' } |
                ForEach-Object Name) | Select-Object -First 30) -join ' | '
}

function Save-Dump {
    if (-not $script:frame) { return '' }
    try {
        $nodes = Read-Frame
        New-Item -ItemType Directory -Force (Split-Path $dumpPath) | Out-Null
        $lines = foreach ($node in $nodes) {
            $text = if ($node.HasText -and $node.Role -eq 'text') { " = '$([JavaBridge]::Text($node))'" } else { '' }
            "$node$text"
        }
        $lines | Set-Content $dumpPath -Encoding UTF8
        " What it showed is in $dumpPath on the gateway."
    }
    catch { '' }
}

# Dialogs eCatcher opens over itself (an error, a question): their text, and their buttons.
function Get-Dialogs {
    $ids = [int[]]@(Get-Ecatcher | ForEach-Object Id)
    if (-not $ids) { return }
    foreach ($window in [JavaBridge]::Windows($ids)) {
        if ($window.Key -eq $script:frame -or -not [JavaBridge]::Visible($window.Key)) { continue }
        $nodes = [JavaBridge]::Read($window.Key)
        $buttons = @($nodes | Where-Object { $_.Role -eq 'push button' -and $_.Has('showing') })
        if (-not $buttons) { continue }
        [pscustomobject]@{
            Title   = $window.Value
            Text    = (@($nodes | Where-Object { $_.Role -in 'label', 'text' -and $_.Has('showing') } | ForEach-Object {
                            if ($_.Name) { $_.Name } elseif ($_.HasText) { [JavaBridge]::Text($_) } }) | Where-Object { $_ }) -join ' '
            Buttons = $buttons
            Boxes   = @($nodes | Where-Object { $_.Role -eq 'check box' -and $_.Has('showing') })
        }
    }
}

# Closes a dialog with its first button, and says what it said.
function Close-Dialog {
    $dialog = @(Get-Dialogs)[0]
    if (-not $dialog) { return '' }
    [void][JavaBridge]::Act($dialog.Buttons[0])
    $dialog.Text
}

function Wait-Until([scriptblock]$condition, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        [JavaBridge]::Pump(700)
        $result = & $condition
        if ($result) { return $result }
    }
    $null
}

# ---------------------------------------------------------------- doing it

function Sign-In {
    $account, $user = $env:EASYVPN_ACCOUNT, $env:EASYVPN_USER
    if (-not $account -or -not $user) {
        throw 'eCatcher asks to sign in to Talk2M: set the facility''s account and user (there, or in the gateway app''s Settings), and its password.'
    }
    $nodes = Read-Frame
    $fields = @($nodes | Where-Object { $_.Role -in 'text', 'password text' -and $_.Has('editable') -and $_.Has('showing') })
    foreach ($pair in @(@('Account:', $account), @('Username:', $user), @('Password:', $env:EASYVPN_PASSWORD))) {
        $label = Find-Node $nodes 'label' $pair[0]
        $field = if ($label) {
            @($fields | Where-Object { [math]::Abs(($_.Y + $_.Height / 2) - ($label.Y + $label.Height / 2)) -lt 15 -and $_.X -gt $label.X })[0]
        }
        if (-not $field) { throw "eCatcher's sign-in shows no $($pair[0].TrimEnd(':')) box." }
        # A password eCatcher remembers stays, when the facility has none.
        if ($pair[0] -eq 'Password:' -and -not $pair[1]) { continue }
        if (-not [JavaBridge]::SetText($field, $pair[1])) { throw "eCatcher's $($pair[0].TrimEnd(':')) box could not be filled in." }
    }
    $logIn = Find-Node $nodes 'push button' 'Log In'
    if (-not $logIn -or -not [JavaBridge]::Act($logIn)) { throw 'eCatcher''s Log In could not be pressed.' }
    Say "Signing in to Talk2M as $user (account $account)."

    $view = Wait-Until {
        $now = Get-View (Read-Frame)
        if ($now -ne 'login') { return $now }
        if (Get-Dialogs) { return 'dialog' }
    } 60
    if ($view -eq 'main') { Say 'Signed in to Talk2M.'; return }
    if ($view -eq 'dialog') { throw "eCatcher did not sign in: $(Close-Dialog)" }
    if (-not $view) { throw "eCatcher did not sign in within 60 s. It shows: $(Labels (Read-Frame))" }
    Assert-View $view
    throw "eCatcher did not sign in. It shows: $(Labels (Read-Frame))"
}

function Find-SearchBox($nodes) {
    @($nodes | Where-Object {
            $_.Role -eq 'text' -and $_.Has('editable') -and $_.Has('single line') -and $_.Has('showing') -and $_.Parent.Role -ne 'combo box' })[0]
}

# The Ewon's row selected, and the button that follows it; $null when eCatcher does not list it.
function Select-Ewon([string]$ewon) {
    $search = Find-SearchBox (Read-Frame)
    if ($search) { [void][JavaBridge]::SetText($search, $ewon) }

    $cell = Wait-Until {
        $now = Read-Frame
        @($now | Where-Object { $_.Parent.Role -in 'table', 'list' -and $_.Parent.Has('showing') -and $_.Name -ieq $ewon })[0]
    } 20
    if (-not $cell) { return $null }

    [JavaBridge]::Select($cell.Parent, $cell.Index)
    $button = Wait-Until {
        $found = Find-ActionButton (Read-Frame)
        if ($found -and $found.Name -ne 'No action') { return $found }
    } 5
    if (-not $button) { $button = Find-ActionButton (Read-Frame) }
    [pscustomobject]@{ Cell = $cell; Button = $button }
}

function Clear-Search {
    $search = Find-SearchBox (Read-Frame)
    if ($search) { [void][JavaBridge]::SetText($search, '') }
}

# When disconnecting, eCatcher may ask for a line for the Ewon's logbook: it is left out.
function Skip-Logbook {
    foreach ($dialog in @(Get-Dialogs)) {
        $never = @($dialog.Boxes | Where-Object { $_.Name -match '(don.?t|do not|never).*(ask|show)' -and -not $_.Has('checked') })[0]
        if ($never) { [void][JavaBridge]::Act($never) }
        $skip = @($dialog.Buttons | Where-Object { $_.Name -match '^(cancel|no|skip|close|not now|later)$' })[0]
        if (-not $skip) { $skip = $dialog.Buttons[-1] }
        [void][JavaBridge]::Act($skip)
        Say "Answered eCatcher's ""$($dialog.Title)"" with $($skip.Name)."
    }
}

function Connect([string]$ewon) {
    $view = Get-View (Read-Frame)
    Assert-View $view
    if ($view -ne 'main') { Sign-In }

    $selected = Select-Ewon $ewon
    if (-not $selected -and $env:EASYVPN_ACCOUNT) {
        # Signed in to another account, perhaps: in again, with the facility's.
        Say "eCatcher lists no Ewon called $ewon; signing in again, to the facility's account."
        Stop-Ecatcher
        $script:frame = Start-Ecatcher
        Sign-In
        $selected = Select-Ewon $ewon
    }
    if (-not $selected) {
        throw "eCatcher lists no Ewon called $ewon in account $env:EASYVPN_ACCOUNT. Give the facility the Ewon's name as eCatcher shows it."
    }

    switch ($selected.Button.Name) {
        'Disconnect' { Say "$ewon is connected in eCatcher."; return }
        'Connect' { }
        'Wake up' { throw "$ewon is offline until woken up: eCatcher can send it an SMS (Wake up), which EasyVPN leaves to a person." }
        default { throw "$ewon is offline in eCatcher, so it cannot be connected." }
    }
    if (-not [JavaBridge]::Act($selected.Button)) { throw "eCatcher's Connect could not be pressed." }
    Say "Pressed Connect for $ewon in eCatcher."

    $done = Wait-Until {
        $now = Read-Frame
        if ((Find-ActionButton $now).Name -eq 'Disconnect' -or (Test-Connected $now)) { return 'connected' }
        if (Get-Dialogs) { return 'dialog' }
    } 120
    if ($done -eq 'connected') { Say "eCatcher has connected $ewon."; return }
    if ($done -eq 'dialog') { throw "eCatcher did not connect ${ewon}: $(Close-Dialog)" }
    throw "eCatcher did not show $ewon connected within 120 s. It shows: $(Labels (Read-Frame))"
}

function Disconnect([string]$ewon) {
    $nodes = Read-Frame
    if ((Get-View $nodes) -ne 'main') { Say 'eCatcher is not signed in to Talk2M, so nothing is connected.'; return }
    if (Find-Node $nodes 'label' 'No active connection.') { Say 'eCatcher has nothing connected.'; return }

    $selected = Select-Ewon $ewon
    if ($selected.Button.Name -eq 'Disconnect') {
        if (-not [JavaBridge]::Act($selected.Button)) { throw "eCatcher's Disconnect could not be pressed." }
        Say "Pressed Disconnect for $ewon in eCatcher."
    }
    else {
        # Another Ewon, then: the active connection's own Disconnect.
        $link = Find-Node (Read-Frame) 'label' 'Disconnect'
        if (-not $link -or -not [JavaBridge]::Act($link)) {
            throw "eCatcher has a connection other than $ewon, which could not be disconnected. It shows: $(Labels (Read-Frame))"
        }
        Say 'Pressed Disconnect for the connection eCatcher has.'
    }

    $done = Wait-Until {
        Skip-Logbook
        if (Find-Node (Read-Frame) 'label' 'No active connection.') { return $true }
    } 45
    if (-not $done) { throw "eCatcher did not show the connection ended within 45 s. It shows: $(Labels (Read-Frame))" }
    Say "eCatcher has disconnected $ewon."
}

# ---------------------------------------------------------------- the action

[JavaBridge]::Start()
$script:frame = Find-Frame
$running = (Get-Ecatcher).Count -gt 0

if ($Action -eq 'Dump') {
    if (-not $script:frame) {
        Say $(if ($running) { 'eCatcher runs, but the accessibility bridge cannot reach it: start it again (and this as administrator).' } else { 'eCatcher is not running here.' })
        exit 1
    }
    Say "Saved.$(Save-Dump)"
    Get-Content $dumpPath | ForEach-Object { Say $_ }
    exit 0
}

if (-not $script:frame) {
    if ($Action -eq 'Disconnect') {
        if (-not $running) { Say 'eCatcher is not running on this desktop.'; exit $notHere }
        # Out of the bridge's reach: closing it ends its connection all the same.
        Stop-Ecatcher
        Say 'Closed eCatcher, which the accessibility bridge could not reach, so nothing is connected.'
        exit 0
    }
    if (-not $running -and -not $Start) { Say 'eCatcher is not running on this desktop.'; exit $notHere }
    if ($running) {
        Say 'eCatcher runs without the accessibility bridge; starting it again with it.'
        Stop-Ecatcher
    }
    $script:frame = Start-Ecatcher
}

if ([JavaBridge]::IsIconic($script:frame) -or -not [JavaBridge]::Visible($script:frame)) {
    # Shown, so it lays its views out, and says what they show.
    [void][JavaBridge]::ShowWindow($script:frame, 9)
    [JavaBridge]::Pump(1500)
}

$ewon = "$env:EASYVPN_EWON".Trim()
if (-not $ewon) { throw 'Set the facility''s Ewon, as eCatcher lists it.' }
try {
    if ($Action -eq 'Connect') { Connect $ewon } else { Disconnect $ewon }
}
finally {
    try { Clear-Search } catch { }
}
exit 0
