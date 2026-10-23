using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EasyVPN.Agent;

/// <summary>
/// This workstation's end of the tunnel. gost, in tungo mode, keeps the "EasyVPN" adapter up and
/// carries whatever is routed to it - TCP and UDP - over TLS to the gateway's relay. The adapter
/// stays put whichever facility is connected; what changes is which networks network.ps1 routes
/// to it, and which DNS names it sends to the facility.
/// </summary>
public sealed class Tunnel(RecentLog log)
{
    public const string Adapter = "EasyVPN";

    // A /30 from the benchmarking range, which no facility should be using.
    private const string AdapterAddress = "198.18.255.1/30";

    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "network.ps1");

    private readonly GostProcess _gost = new(Path.Combine(AgentSettings.Folder, "tunnel.json"), log.Add);
    private IReadOnlyList<string> _routes = [];

    public bool IsUp => _gost.IsRunning;

    public void Start(AgentSettings settings, int relayPort) => _gost.Start(new
    {
        Log = new { Level = "warn" },
        Services = new[]
        {
            new
            {
                Name = "tunnel",
                Addr = ":0",
                Handler = new { Type = "tungo", Chain = "gateway" },
                Listener = new { Type = "tungo", Metadata = new { Name = Adapter, Net = AdapterAddress, Mtu = 1420 } },
            },
        },
        Chains = new[]
        {
            new
            {
                Name = "gateway",
                Hops = new[]
                {
                    new
                    {
                        Name = "gateway",
                        Nodes = new[]
                        {
                            new
                            {
                                Name = "gateway",
                                Addr = $"{settings.Host}:{relayPort}",
                                Connector = new { Type = "relay", Auth = new { Username = Defaults.RelayUser, Password = settings.AccessKey } },
                                Dialer = new
                                {
                                    Type = "tls",
                                    Tls = new { Secure = true, ServerName = Defaults.CertificateName, CaFile = AgentSettings.CertificateFile },
                                },
                            },
                        },
                    },
                },
            },
        },
    });

    public void Stop() => _gost.Stop();

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
        var code = await PowerShell.RunFileAsync(Script, [action], environment, TimeSpan.FromMinutes(1), line => log.Add(last = line), ct);
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
                log.Add($"Skipped the route {route}: only IPv4 networks of /8 or narrower are routed");
                continue;
            }
            // Its own traffic to the gateway must never go into the tunnel it is carrying.
            if (gateway.Any(network.Contains))
            {
                log.Add($"Skipped the route {route}: the gateway is in it");
                continue;
            }
            foreach (var (name, local) in LocalNetworks().Where(l => l.Network.Contains(network.BaseAddress) || network.Contains(l.Network.BaseAddress)))
                log.Add($"{route} overlaps {local} on {name}. While connected, those addresses are the facility's.");
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
