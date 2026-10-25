using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasyVPN.Gateway;

/// <summary>
/// Runs the EasyVPN Gateway app - or, with <c>--program</c>, another program - in a signed-in user's
/// desktop session, for what only works there: driving a VPN client's window. The service runs as
/// SYSTEM, which may start a process as the user of any session; its connectors reach this as
/// <c>EasyVPN.Gateway.exe desktop &lt;app arguments&gt;</c>.
/// Sessions are tried console first, and an app that found nothing to drive in one (exit code
/// <see cref="NotHere"/>) is tried in the next; when none had it, so does this. What it prints
/// comes back, and its exit code.
/// The facility's EASYVPN_ settings, the password among them, go to it in its environment.
/// </summary>
public static class DesktopSession
{
    /// <summary>The app's exit code for "not in this session".</summary>
    public const int NotHere = 3;

    private const int PrivilegeNotHeld = 1314;

    /// <param name="args">
    /// The app's arguments, after any of these:
    /// <c>--usable-only</c>, only the sessions someone is connected to (at the console, or over
    /// Remote Desktop), as nothing can be typed into the others;
    /// <c>--close-elsewhere PREFIX</c>, programs whose names start so are closed in every other
    /// session before the app runs: a VPN client left on a desktop nobody uses, which would keep
    /// another copy from starting;
    /// <c>--program PATH</c>, that program runs instead of the app, with the rest as its arguments:
    /// a script in 32-bit PowerShell, say, for a client only a 32-bit program can reach.
    /// </param>
    public static int RunApp(string[] args, TimeSpan timeout)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "EasyVPN.GatewayApp.exe");
        var usableOnly = false;
        string? closeElsewhere = null;
        var rest = new List<string>(args);
        while (rest.Count > 0)
        {
            if (rest[0] == "--usable-only") usableOnly = true;
            else if (rest[0] == "--close-elsewhere" && rest.Count > 1)
            {
                closeElsewhere = rest[1];
                rest.RemoveAt(0);
            }
            else if (rest[0] == "--program" && rest.Count > 1)
            {
                app = rest[1];
                rest.RemoveAt(0);
            }
            else break;
            rest.RemoveAt(0);
        }
        args = [.. rest];
        if (!File.Exists(app))
        {
            Console.WriteLine($"{Path.GetFileName(app)} is not where the gateway looked for it ({app}).");
            return 1;
        }

        var tried = false;
        foreach (var (session, active, locked) in Sessions())
        {
            if (usableOnly && !active) continue;
            if (!WTSQueryUserToken(session, out var token))
            {
                // Only SYSTEM may do this; run by hand, the app runs here, in this session.
                if (Marshal.GetLastWin32Error() == PrivilegeNotHeld) return RunHere(app, args, timeout);
                continue; // Nobody signed in there.
            }
            tried = true;
            if (closeElsewhere is not null) CloseElsewhere(closeElsewhere, session);
            // Whose desktop, so what follows can be told apart in the gateway's activity.
            Console.WriteLine($"On {Describe(session)}'s desktop (session {session}, {(active ? Station(session) + (locked ? ", locked" : "") : "disconnected")}):");
            int code;
            using (var user = new SafeAccessTokenHandle(token))
            using (var full = Elevated(user))
                code = RunAs(full.IsInvalid ? user : full, app, args, timeout);
            if (code != NotHere) return code;
        }

        if (tried) return NotHere;
        Console.WriteLine(usableOnly
            ? "Nobody is at a desktop of this gateway (signed in and connected, at the console or over Remote Desktop). Set the gateway to sign in by itself."
            : "Nobody is signed in to this gateway's desktop. Set the gateway to sign in by itself, or sign in there.");
        return 1;
    }

    /// <summary>Closes the programs whose names start with <paramref name="prefix"/> in every session but <paramref name="keep"/>.</summary>
    private static void CloseElsewhere(string prefix, uint keep)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == 0 || process.SessionId == keep || !process.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var session = (uint)process.SessionId;
                    process.Kill();
                    Console.WriteLine($"Closed {process.ProcessName} on {Describe(session)}'s desktop (session {session}), where nobody could use it.");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Gone already, or not to be closed.
                }
            }
        }
    }

    /// <summary>
    /// An administrator's session runs with the limited half of a pair of tokens; SYSTEM may have the
    /// full one. The app runs with it, so it can reach a VPN client that runs as administrator, and
    /// start one as such without Windows asking first.
    /// </summary>
    private static SafeAccessTokenHandle Elevated(SafeAccessTokenHandle token)
    {
        const int tokenElevationType = 18, tokenLinkedToken = 19, limited = 3;
        if (GetTokenInformation(token, tokenElevationType, out int type, sizeof(int), out _) && type == limited
            && GetTokenInformation(token, tokenLinkedToken, out IntPtr linked, IntPtr.Size, out _))
            return new SafeAccessTokenHandle(linked);
        return SafeAccessTokenHandle.InvalidHandle;
    }

    /// <summary>
    /// The sessions with someone in them, those someone is at first - the console's, then Remote
    /// Desktop's - and those left disconnected last: a client started there would be out of
    /// everyone's sight, and could not be typed into.
    /// </summary>
    private static List<(uint Session, bool Active, bool Locked)> Sessions()
    {
        var sessions = new List<(uint Session, bool Active, bool Locked)>();
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                    if (info.SessionId != 0 && info.State is WtsActive or WtsDisconnected)
                        sessions.Add((info.SessionId, info.State == WtsActive, Locked(info.SessionId)));
                }
            }
            finally
            {
                WTSFreeMemory(list);
            }
        }
        // Unlocked before locked: only a desktop someone could work at takes typing.
        var console = WTSGetActiveConsoleSessionId();
        return [.. sessions.OrderByDescending(s => s.Active).ThenBy(s => s.Locked).ThenByDescending(s => s.Session == console)];
    }

    /// <summary>Whether a session's screen is locked, so nothing can be typed into it.</summary>
    private static bool Locked(uint session)
    {
        const int sessionInfoEx = 25, flagsOffset = 16, lockedFlag = 0;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, sessionInfoEx, out var buffer, out _)) return false;
        try
        {
            // WTSINFOEXW: its level, then (aligned to 8, as it holds 64-bit times) WTSINFOEX_LEVEL1_W:
            // session id, state, flags.
            return Marshal.ReadInt32(buffer, flagsOffset) == lockedFlag;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>The user signed in to a session, as DOMAIN\name.</summary>
    private static string Describe(uint session)
    {
        var user = Query(session, WtsUserName);
        var domain = Query(session, WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
    }

    /// <summary>Where a session is shown: the console, or Remote Desktop.</summary>
    private static string Station(uint session) =>
        Query(session, WtsWinStationName) is { Length: > 0 } name && !name.Equals("Console", StringComparison.OrdinalIgnoreCase) ? "Remote Desktop" : "the console";

    private static string Query(uint session, int info)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, info, out var buffer, out _)) return "";
        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static int RunAs(SafeAccessTokenHandle token, string app, string[] args, TimeSpan timeout)
    {
        if (!CreateEnvironmentBlock(out var userBlock, token, false)) throw new Win32Exception();
        Dictionary<string, string> environment;
        try
        {
            environment = ReadBlock(userBlock);
        }
        finally
        {
            DestroyEnvironmentBlock(userBlock);
        }
        foreach (var (name, value) in Ours()) environment[name] = value;
        var block = Marshal.StringToHGlobalUni(string.Concat(environment.Select(v => $"{v.Key}={v.Value}\0")) + "\0");

        var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var read, out var write, ref inherit, 0)) throw new Win32Exception();
        SetHandleInformation(read, HandleFlagInherit, 0);
        try
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default",
                Flags = StartfUseStdHandles,
                StdOutput = write,
                StdError = write,
            };
            var commandLine = new StringBuilder(string.Join(" ", new[] { app }.Concat(args).Select(Quote)));
            if (!CreateProcessAsUser(token, app, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateUnicodeEnvironment | CreateNoWindow, block, Path.GetDirectoryName(app), ref startup, out var process))
                throw new Win32Exception();
            CloseHandle(write);
            write = IntPtr.Zero;

            using var output = new StreamReader(new FileStream(new SafeFileHandle(read, ownsHandle: true), FileAccess.Read), Encoding.UTF8);
            read = IntPtr.Zero;
            var relay = Task.Run(() =>
            {
                for (var line = output.ReadLine(); line is not null; line = output.ReadLine()) Console.WriteLine(line);
            });
            try
            {
                if (WaitForSingleObject(process.Process, (uint)timeout.TotalMilliseconds) != 0)
                {
                    TerminateProcess(process.Process, 1);
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(app)} was stopped after {timeout.TotalSeconds:0} s.");
                    return 1;
                }
                relay.Wait(TimeSpan.FromSeconds(5));
                GetExitCodeProcess(process.Process, out var code);
                return (int)code;
            }
            finally
            {
                CloseHandle(process.Process);
                CloseHandle(process.Thread);
            }
        }
        finally
        {
            if (write != IntPtr.Zero) CloseHandle(write);
            if (read != IntPtr.Zero) CloseHandle(read);
            Marshal.FreeHGlobal(block);
        }
    }

    private static int RunHere(string app, string[] args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(app) { UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) Console.WriteLine(e.Data);
        };
        process.BeginOutputReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill();
            return 1;
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    private static IEnumerable<(string, string)> Ours() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => name.StartsWith("EASYVPN_", StringComparison.OrdinalIgnoreCase))
            .Select(name => (name, Environment.GetEnvironmentVariable(name) ?? ""));

    private static Dictionary<string, string> ReadBlock(IntPtr block)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var entry = Marshal.PtrToStringUni(block); !string.IsNullOrEmpty(entry); entry = Marshal.PtrToStringUni(block))
        {
            var equals = entry.IndexOf('=', 1);
            if (equals > 0) environment[entry[..equals]] = entry[(equals + 1)..];
            block += (entry.Length + 1) * sizeof(char);
        }
        return environment;
    }

    /// <summary>One argument as Windows' command-line parsing reads it back.</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    // ---------------------------------------------------------------- Windows

    private const int WtsActive = 0;
    private const int WtsDisconnected = 4;
    private const int WtsWinStationName = 6, WtsUserName = 5, WtsDomainName = 7;
    private const uint HandleFlagInherit = 1;
    private const int StartfUseStdHandles = 0x100;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int info, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint session, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out IntPtr info, int length, out int returned);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment,
        string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
