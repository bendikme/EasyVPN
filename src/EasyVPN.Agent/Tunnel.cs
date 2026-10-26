using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace EasyVPN.Agent;

/// <summary>
/// This workstation's end of the tunnel: the "EasyVPN" adapter, and its link to the gateway - a
/// WebSocket on the gateway's API port, with its access key and pinned certificate - carrying the
/// packets routed to the adapter, all of them, ping as well as TCP and UDP. The gateway puts them
/// out on its own adapter, and into the connected facility's VPN, which it shares with the tunnel.
/// So the adapter works as the VPN client's own would on this PC, whichever VPN client it is. The
/// adapter stays put whichever facility is connected; what changes is which networks network.ps1
/// routes to it, and which DNS names it sends to the facility. A link that drops is made again,
/// the adapter staying as it is meanwhile.
/// </summary>
public sealed class Tunnel(RecentLog log)
{
    public const string Adapter = Defaults.TunnelAdapter;

    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "network.ps1");
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(10);

    private readonly RecentLog _log = log;
    private readonly Lock _sync = new();
    private IReadOnlyList<string> _routes = [];
    private Task? _run;
    private CancellationTokenSource? _stop;
    private object? _gateway;
    private TunnelLease? _lease;
    private volatile PacketLink? _link;
    private volatile string? _problem;

    /// <summary>The adapter's name and identity; others for a test.</summary>
    public string AdapterName { get; init; } = Adapter;
    public string AdapterGuid { get; init; } = Defaults.TunnelAdapterGuid;

    /// <summary>NetBIOS off and the names set on the adapter (network.ps1 Prepare): not for a test's.</summary>
    public bool Prepare { get; init; } = true;

    /// <summary>The link to the gateway is up: what is routed to the adapter gets there.</summary>
    public bool IsUp => _link is not null;

    /// <summary>Why it is not up, when it is not.</summary>
    public string? Problem => _problem;

    /// <summary>Makes the adapter and links it to the gateway, unless that is so already.</summary>
    /// <param name="lease">This PC's address on the tunnel, which the gateway gave it.</param>
    public void Start(GatewayClient gateway, TunnelLease lease) => Start(gateway.OpenTunnelAsync, lease, gateway);

    /// <param name="open">Opens the link to the gateway.</param>
    /// <param name="gateway">Which gateway: linked to another, the tunnel starts over.</param>
    public void Start(Func<CancellationToken, Task<WebSocket>> open, TunnelLease lease, object gateway)
    {
        lock (_sync)
        {
            if (_run is { IsCompleted: false } && ReferenceEquals(_gateway, gateway) && Equals(_lease, lease)) return;
            StopLocked();
            (_gateway, _lease, _stop) = (gateway, lease, new CancellationTokenSource());
            var ct = _stop.Token;
            _run = Task.Run(() => RunAsync(open, lease, ct), CancellationToken.None);
        }
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    private void StopLocked()
    {
        if (_run is null) return;
        _stop!.Cancel();
        _run.Wait(TimeSpan.FromSeconds(10));
        _stop.Dispose();
        (_run, _stop, _gateway, _lease) = (null, null, null, null);
    }

    private async Task RunAsync(Func<CancellationToken, Task<WebSocket>> open, TunnelLease lease, CancellationToken ct)
    {
        var address = IPAddress.Parse(lease.Address);
        Wintun? adapter = null;
        Thread? reader = null;
        try
        {
            while (adapter is null)
            {
                try
                {
                    adapter = Wintun.Create(AdapterName, AdapterGuid, address, lease.PrefixLength, Defaults.TunnelMtu);
                }
                catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or BadImageFormatException)
                {
                    Say($"The {AdapterName} adapter could not be made: {ex.Message}");
                    await Task.Delay(LongestWait, ct);
                }
            }
            if (Prepare) _ = PrepareAsync();
            var made = adapter;
            reader = new Thread(() => Read(made)) { IsBackground = true, Name = "EasyVPN tunnel" };
            reader.Start();

            var own = IPv4.Of(address);
            var wait = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    using var socket = await open(ct);
                    var link = new PacketLink(socket, packet => Deliver(made, own, packet));
                    _link = link;
                    Say(null);
                    _log.Add($"Tunnel to the gateway up, as {lease.Address}");
                    wait = TimeSpan.FromSeconds(1);
                    await link.RunAsync(ct);
                    Say("The gateway closed the tunnel");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidOperationException or WebSocketException or HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    Say($"The tunnel to the gateway: {ex.Message}");
                }
                finally
                {
                    _link = null;
                }
                await Task.Delay(wait, ct);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestWait.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            _link = null;
            adapter?.Dispose();
            reader?.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>What Windows routes to the adapter, to the gateway; dropped while the link is down.</summary>
    private void Read(Wintun adapter)
    {
        var packet = new byte[Wintun.MaxPacket];
        int length;
        while ((length = adapter.Read(packet)) > 0) _link?.Send(packet.AsSpan(0, length));
    }

    /// <summary>What the gateway sends, into Windows: only what is for this PC's own address on the tunnel.</summary>
    private static bool Deliver(Wintun adapter, uint own, ReadOnlySpan<byte> packet) =>
        IPv4.TryRead(packet, out _, out var destination) && destination == own && adapter.Write(packet);

    /// <summary>A problem said once, not at each try; null when it is over.</summary>
    private void Say(string? problem)
    {
        if (problem == _problem) return;
        _problem = problem;
        if (problem is not null) _log.Add(problem);
    }

    /// <summary>
    /// NetBIOS off on the new adapter. Windows broadcasts NetBIOS on every adapter that has it, and
    /// on this one each broadcast would try to go through the tunnel - every second, for nothing.
    /// And the networks Windows remembered for the adapters made before go, so this one is "EasyVPN".
    /// </summary>
    private async Task PrepareAsync()
    {
        var last = "";
        var code = await PowerShell.RunFileAsync(Script, ["Prepare"], new Dictionary<string, string>(), TimeSpan.FromMinutes(1), line => last = line, CancellationToken.None);
        if (code != 0) _log.Add($"Could not prepare the {Adapter} adapter: {last}");
    }

    /// <summary>Routes the facility's networks to the adapter and its DNS names to its servers.</summary>
    public Task ApplyAsync(FacilityInfo facility, string gatewayHost, CancellationToken ct)
    {
        var routes = Routes(facility, gatewayHost);
        if (routes.Count == 0) throw new InvalidOperationException($"None of {facility.Name}'s networks can be routed from this PC.");
        return RunAsync("Apply", routes, facility.Dns, ct);
    }

    /// <summary>Takes away whatever <see cref="ApplyAsync"/> set up, including DNS rules left by an earlier run.</summary>
    public Task ClearAsync() => RunAsync("Clear", [], null, CancellationToken.None);

    private async Task RunAsync(string action, IReadOnlyList<string> routes, DnsRule? dns, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            // What was routed before goes - and so does anything about to be added, in case it is still there.
            ["EASYVPN_OLD_ROUTES"] = string.Join(',', _routes.Union(routes)),
            ["EASYVPN_ROUTES"] = string.Join(',', routes),
            ["EASYVPN_DNS_DOMAINS"] = string.Join(',', dns?.Domains ?? []),
            ["EASYVPN_DNS_SERVERS"] = string.Join(',', dns?.Servers ?? []),
        };
        _routes = routes;
        var last = "";
        var code = await PowerShell.RunFileAsync(Script, [action], environment, TimeSpan.FromMinutes(1), line => _log.Add(last = line), ct);
        if (code != 0) throw new InvalidOperationException($"Setting up this PC's routes failed: {last}");
    }

    /// <summary>The facility's routes that are safe to add, with a warning for any that hide a local network.</summary>
    private List<string> Routes(FacilityInfo facility, string gatewayHost)
    {
        IPAddress[] gateway;
        try
        {
            gateway = Dns.GetHostAddresses(gatewayHost);
        }
        catch (SocketException)
        {
            gateway = [];
        }

        var routes = new List<string>();
        foreach (var route in facility.Routes)
        {
            if (!IPNetwork.TryParse(route, out var network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork || network.PrefixLength < 8)
            {
                _log.Add($"Skipped the route {route}: only IPv4 networks of /8 or narrower are routed");
                continue;
            }
            // Its own traffic to the gateway must never go into the tunnel it is carrying.
            if (gateway.Any(network.Contains))
            {
                _log.Add($"Skipped the route {route}: the gateway is in it");
                continue;
            }
            foreach (var (name, local) in LocalNetworks().Where(l => l.Network.Contains(network.BaseAddress) || network.Contains(l.Network.BaseAddress)))
                _log.Add($"{route} overlaps {local} on {name}. While connected, those addresses are the facility's.");
            routes.Add(network.ToString());
        }
        return routes;
    }

    private static IEnumerable<(string Name, IPNetwork Network)> LocalNetworks() =>
        from nic in NetworkInterface.GetAllNetworkInterfaces()
        where nic.OperationalStatus == OperationalStatus.Up
            && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && nic.Name != Adapter
        from address in nic.GetIPProperties().UnicastAddresses
        where address.Address.AddressFamily == AddressFamily.InterNetwork
        select (nic.Name, new IPNetwork(address.Address, address.PrefixLength));
}
