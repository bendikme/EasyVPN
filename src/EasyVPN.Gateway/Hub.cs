using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's end of the tunnel: this machine's "EasyVPN-Gateway" adapter, and the workstations'
/// links to it - WebSockets on the API port, with its access key and certificate, each carrying one
/// workstation's IP packets (<see cref="PacketLink"/>). What a workstation sends comes out of the
/// adapter as if from its own address on the tunnel, and only from that address: a packet that says
/// it is from anywhere else is dropped. From the adapter it goes wherever the gateway sends it - into
/// the facility's VPN, once <see cref="Sharing"/> shares it - and what comes back for a workstation's
/// address goes to that workstation's link.
///
/// The adapter is there as long as the gateway runs, so what Windows has set up for it stays put;
/// what changes with the facility is only what it is shared with.
/// </summary>
public sealed class Hub(RecentLog log) : IDisposable
{
    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<uint, PacketLink> _links = new();
    private volatile Wintun? _adapter;
    private Thread? _reader;
    private int _launches;

    public string AdapterName { get; init; } = Defaults.GatewayAdapter;
    public string AdapterGuid { get; init; } = Defaults.GatewayAdapterGuid;
    public IPNetwork Network { get; init; } = Defaults.TunnelNetwork;
    public IPAddress Address { get; init; } = IPAddress.Parse(Defaults.TunnelHub);

    /// <summary>
    /// How many times the adapter has been made. Each time it is made again, what it was shared
    /// with goes with the old one.
    /// </summary>
    public int Launches => Volatile.Read(ref _launches);

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _adapter is not null && _reader is { IsAlive: true };
        }
    }

    /// <summary>The workstations linked now.</summary>
    public int Links => _links.Count;

    /// <summary>Makes the adapter, unless it is there.</summary>
    /// <exception cref="InvalidOperationException">It could not be made: what Windows or Wintun said.</exception>
    public void Start()
    {
        lock (_sync)
        {
            if (_adapter is not null && _reader is { IsAlive: true }) return;
            StopLocked();
            var adapter = Wintun.Create(AdapterName, AdapterGuid, Address, Network.PrefixLength, Defaults.TunnelMtu);
            _adapter = adapter;
            _reader = new Thread(() => Read(adapter)) { IsBackground = true, Name = "EasyVPN tunnel hub" };
            _reader.Start();
            Interlocked.Increment(ref _launches);
            log.Add($"The tunnel for the workstations is up, on the {AdapterName} adapter at {Address}");
        }
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    public void Dispose() => Stop();

    private void StopLocked()
    {
        _adapter?.Dispose();
        _reader?.Join(TimeSpan.FromSeconds(5));
        (_adapter, _reader) = (null, null);
    }

    /// <summary>A workstation's link, at its address on the tunnel, until either end closes it.</summary>
    public async Task RunLinkAsync(HttpContext context, TunnelLease lease)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!IsRunning)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var address = IPv4.Of(IPAddress.Parse(lease.Address));
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var link = await AttachAsync(socket, address);
        try
        {
            await link.RunAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException or IOException)
        {
            // The workstation went; it links again when it can.
        }
        finally
        {
            _links.TryRemove(KeyValuePair.Create(address, link));
        }
    }

    /// <summary>
    /// The link for a workstation's address. One that was there for it already - the same PC,
    /// linked again before the old one timed out - is closed.
    /// </summary>
    public async Task<PacketLink> AttachAsync(WebSocket socket, uint address)
    {
        var link = new PacketLink(socket, packet => FromWorkstation(address, packet));
        PacketLink? old = null;
        _links.AddOrUpdate(address, link, (_, previous) =>
        {
            old = previous;
            return link;
        });
        if (old is not null)
        {
            try
            {
                await old.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Linked again", CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Gone already.
            }
        }
        return link;
    }

    private bool FromWorkstation(uint address, ReadOnlySpan<byte> packet) =>
        IPv4.TryRead(packet, out var source, out _)
        && source == address
        && _adapter is { } adapter
        && adapter.Write(packet);

    /// <summary>What Windows sends out of the adapter, to the link of the workstation it is for.</summary>
    private void Read(Wintun adapter)
    {
        var packet = new byte[Wintun.MaxPacket];
        int length;
        while ((length = adapter.Read(packet)) > 0)
        {
            if (IPv4.TryRead(packet.AsSpan(0, length), out _, out var destination) && _links.TryGetValue(destination, out var link))
                link.Send(packet.AsSpan(0, length));
        }
    }
}
