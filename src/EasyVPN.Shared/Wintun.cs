using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyVPN;

/// <summary>
/// A Wintun adapter and its session: IP packets out of Windows and back in, for the tunnel's two
/// ends. wintun.dll - WireGuard's, signed by them - sits beside the exe and is loaded from there
/// only. The adapter is made with a fixed identity, so Windows sees the same network on it each
/// time, and goes when this is disposed, or when the process ends.
/// </summary>
public sealed class Wintun : IDisposable
{
    /// <summary>The largest IP packet Wintun carries.</summary>
    public const int MaxPacket = 0xFFFF;

    private const int RingCapacity = 0x400000; // 4 MiB each way
    private const int NoMoreItems = 259, HandleEof = 38, BufferOverflow = 111;

    private readonly IntPtr _adapter;
    private readonly IntPtr _session;
    private readonly IntPtr _readEvent;
    private readonly ManualResetEvent _stop = new(false);
    private int _users;
    private volatile bool _closed;

    static Wintun() => NativeLibrary.SetDllImportResolver(typeof(Wintun).Assembly, (name, _, _) =>
        name == "wintun.dll" ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "wintun.dll")) : IntPtr.Zero);

    private Wintun(IntPtr adapter, IntPtr session)
    {
        _adapter = adapter;
        _session = session;
        _readEvent = WintunGetReadWaitEvent(session);
    }

    public string Name { get; private init; } = "";

    /// <summary>
    /// Makes the adapter, starts its session, and gives it its address and MTU.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wintun could not make it: what Windows said.</exception>
    public static Wintun Create(string name, string guid, IPAddress address, int prefixLength, int mtu)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
            throw new InvalidOperationException($"wintun.dll is not beside {Path.GetFileName(Environment.ProcessPath)} ({AppContext.BaseDirectory}).");

        var id = Guid.Parse(guid);
        var adapter = WintunCreateAdapter(name, "Dynatec EasyVPN", ref id);
        if (adapter == IntPtr.Zero)
            throw new InvalidOperationException($"Wintun could not make the \"{name}\" adapter: {new Win32Exception().Message}");
        var session = WintunStartSession(adapter, RingCapacity);
        if (session == IntPtr.Zero)
        {
            var why = new Win32Exception().Message;
            WintunCloseAdapter(adapter);
            throw new InvalidOperationException($"Wintun could not start the \"{name}\" adapter: {why}");
        }

        var wintun = new Wintun(adapter, session) { Name = name };
        try
        {
            WintunGetAdapterLUID(adapter, out var luid);
            SetAddress(luid, address, prefixLength);
            SetMtu(name, mtu);
            return wintun;
        }
        catch
        {
            wintun.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The next packet Windows sends out of the adapter, copied into <paramref name="buffer"/>
    /// (<see cref="MaxPacket"/> long). Blocks until there is one; 0 once the adapter is closed.
    /// One thread reads.
    /// </summary>
    public int Read(byte[] buffer)
    {
        if (!Enter()) return 0;
        try
        {
            while (true)
            {
                var packet = WintunReceivePacket(_session, out var size);
                if (packet != IntPtr.Zero)
                {
                    var length = (int)Math.Min(size, (uint)buffer.Length);
                    Marshal.Copy(packet, buffer, 0, length);
                    WintunReleaseReceivePacket(_session, packet);
                    return length;
                }
                var error = Marshal.GetLastPInvokeError();
                if (error != NoMoreItems) return 0; // the adapter is going (HandleEof), or its ring broke
                var stop = _stop.SafeWaitHandle.DangerousGetHandle();
                if (WaitForMultipleObjects(2, [_readEvent, stop], false, uint.MaxValue) != 0) return 0;
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Hands a packet to Windows, as if it had come in on the adapter. False when it was dropped: the adapter closed, or Windows behind.</summary>
    public bool Write(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet.Length > MaxPacket || !Enter()) return false;
        try
        {
            var buffer = WintunAllocateSendPacket(_session, (uint)packet.Length);
            if (buffer == IntPtr.Zero) return false; // BufferOverflow: Windows is behind; HandleEof: going
            unsafe
            {
                packet.CopyTo(new Span<byte>((void*)buffer, packet.Length));
            }
            WintunSendPacket(_session, buffer);
            return true;
        }
        finally
        {
            Exit();
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _stop.Set();
        // A read or write still in Wintun finishes first: the session is not ended under it.
        var wait = Stopwatch.StartNew();
        while (Volatile.Read(ref _users) > 0 && wait.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        WintunEndSession(_session);
        WintunCloseAdapter(_adapter);
        _stop.Dispose();
    }

    private bool Enter()
    {
        Interlocked.Increment(ref _users);
        if (!_closed) return true;
        Interlocked.Decrement(ref _users);
        return false;
    }

    private void Exit() => Interlocked.Decrement(ref _users);

    // ---------------------------------------------------------------- the adapter's settings

    private static void SetAddress(ulong luid, IPAddress address, int prefixLength)
    {
        var row = new MibUnicastIpAddressRow();
        InitializeUnicastIpAddressEntry(ref row);
        row.Family = 2; // AF_INET
        address.GetAddressBytes().CopyTo(row.Address.AsSpan());
        row.InterfaceLuid = luid;
        row.OnLinkPrefixLength = (byte)prefixLength;
        row.DadState = 4; // IpDadStatePreferred: usable at once
        var result = CreateUnicastIpAddressEntry(ref row);
        if (result is not (0 or 5010)) // 5010: there already
            throw new InvalidOperationException($"Windows did not give the adapter its address {address}/{prefixLength}: {new Win32Exception(result).Message}");
    }

    /// <summary>netsh, as for the adapter's address it would take Windows' whole interface row, which differs between versions.</summary>
    private static void SetMtu(string name, int mtu)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            ArgumentList = { "interface", "ipv4", "set", "subinterface", name, $"mtu={mtu}", "store=active" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var netsh = Process.Start(start)!;
        var said = netsh.StandardOutput.ReadToEnd().Trim();
        netsh.WaitForExit();
        if (netsh.ExitCode != 0) throw new InvalidOperationException($"Windows did not set the \"{name}\" adapter's MTU to {mtu}: {said}");
    }

    // MIB_UNICASTIPADDRESS_ROW, for IPv4: SOCKADDR_INET (28 bytes), then the LUID at 32.
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct MibUnicastIpAddressRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public InAddr Address;
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(60)] public byte OnLinkPrefixLength;
        [FieldOffset(64)] public int DadState;
    }

    [InlineArray(4)]
    private struct InAddr
    {
        private byte _byte;

        public Span<byte> AsSpan() => MemoryMarshal.CreateSpan(ref _byte, 4);
    }

    [DllImport("iphlpapi.dll")]
    private static extern void InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern int CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint milliseconds);

    // ---------------------------------------------------------------- wintun.dll

    [DllImport("wintun.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WintunCreateAdapter(string name, string tunnelType, ref Guid requested);

    [DllImport("wintun.dll")]
    private static extern void WintunCloseAdapter(IntPtr adapter);

    [DllImport("wintun.dll")]
    private static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

    [DllImport("wintun.dll", SetLastError = true)]
    private static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport("wintun.dll")]
    private static extern void WintunEndSession(IntPtr session);

    [DllImport("wintun.dll")]
    private static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    [DllImport("wintun.dll", SetLastError = true)]
    private static extern IntPtr WintunReceivePacket(IntPtr session, out uint size);

    [DllImport("wintun.dll")]
    private static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [DllImport("wintun.dll", SetLastError = true)]
    private static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint size);

    [DllImport("wintun.dll")]
    private static extern void WintunSendPacket(IntPtr session, IntPtr packet);
}
