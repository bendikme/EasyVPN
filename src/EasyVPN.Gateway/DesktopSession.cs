using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasyVPN.Gateway;

/// <summary>
/// Runs the EasyVPN Gateway app in a signed-in user's desktop session, for what only works there:
/// driving a VPN client's window. The service runs as SYSTEM, which may start a process as the user
/// of any session; its connectors reach this as <c>EasyVPN.Gateway.exe desktop &lt;app arguments&gt;</c>.
/// Sessions are tried console first, and an app that found nothing to drive in one (exit code
/// <see cref="NotHere"/>) is tried in the next. What it prints comes back, and its exit code.
/// The facility's EASYVPN_ settings, the password among them, go to it in its environment.
/// </summary>
public static class DesktopSession
{
    /// <summary>The app's exit code for "not in this session".</summary>
    public const int NotHere = 3;

    private const int PrivilegeNotHeld = 1314;

    public static int RunApp(string[] args, TimeSpan timeout)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "EasyVPN.GatewayApp.exe");
        if (!File.Exists(app))
        {
            Console.WriteLine($"The EasyVPN Gateway app is not beside the gateway ({app}).");
            return 1;
        }

        var tried = false;
        foreach (var session in Sessions())
        {
            if (!WTSQueryUserToken(session, out var token))
            {
                // Only SYSTEM may do this; run by hand, the app runs here, in this session.
                if (Marshal.GetLastWin32Error() == PrivilegeNotHeld) return RunHere(app, args, timeout);
                continue; // Nobody signed in there.
            }
            tried = true;
            int code;
            using (var handle = new SafeAccessTokenHandle(token))
                code = RunAs(handle, app, args, timeout);
            if (code != NotHere) return code;
        }

        Console.WriteLine(tried
            ? "None of the desktops signed in to this gateway has it."
            : "Nobody is signed in to this gateway's desktop. Sign in there (or set the gateway to sign in by itself) and start the VPN client.");
        return 1;
    }

    /// <summary>The console's session first, then the others with someone signed in, connected or not.</summary>
    private static List<uint> Sessions()
    {
        var sessions = new List<uint>();
        var console = WTSGetActiveConsoleSessionId();
        if (console != uint.MaxValue) sessions.Add(console);
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                    if (info.SessionId != 0 && info.State is WtsActive or WtsDisconnected && !sessions.Contains(info.SessionId))
                        sessions.Add(info.SessionId);
                }
            }
            finally
            {
                WTSFreeMemory(list);
            }
        }
        return sessions;
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
                    Console.WriteLine($"The EasyVPN Gateway app was stopped after {timeout.TotalSeconds:0} s.");
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

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint session, out IntPtr token);

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
