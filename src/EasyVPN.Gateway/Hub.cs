namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's end of the tunnel: gost taking the workstations' packets in over TLS, at the
/// relay, and putting them out on this machine's "EasyVPN-Gateway" adapter, each workstation at
/// its own address there. From that adapter they go wherever the gateway sends them - into the
/// facility's VPN, once <see cref="Sharing"/> shares it - and the answers come back the same way.
///
/// The relay takes the access key, and takes anything sent through it to the hub and nowhere
/// else. Both run as long as the gateway does, so the adapter, and with it what Windows has set up
/// for it, stays put; what changes with the facility is only what the adapter is shared with.
/// </summary>
public sealed class Hub
{
    private readonly GostProcess _gost;
    private readonly GatewaySettings _settings;
    private readonly GatewayCertificate _certificate;
    private int _launches;

    public Hub(GatewaySettings settings, GatewayCertificate certificate, RecentLog log)
    {
        _settings = settings;
        _certificate = certificate;
        _gost = new(Path.Combine(GatewaySettings.Folder, "hub.json"), log.Add, () => Interlocked.Increment(ref _launches));
    }

    /// <summary>
    /// How many times gost has been started. Each start makes the adapter again, and what it was
    /// shared with goes with the old one.
    /// </summary>
    public int Launches => Volatile.Read(ref _launches);

    public bool IsRunning => _gost.IsRunning;

    public void Start() => _gost.Start(new
    {
        Log = new { Level = "warn" },
        Services = new object[]
        {
            new
            {
                Name = "relay",
                Addr = $"{_settings.ListenAddress}:{_settings.RelayPort}",
                Handler = new { Type = "relay", Auth = new { Username = Defaults.RelayUser, Password = _settings.AccessKey } },
                Listener = new { Type = "tls", Tls = new { _certificate.CertFile, _certificate.KeyFile } },
                // Whatever a workstation asks for, it gets the hub.
                Forwarder = new { Nodes = new[] { new { Name = "hub", Addr = $"127.0.0.1:{Defaults.TunnelHubPort}" } } },
            },
            new
            {
                Name = "hub",
                Addr = $"127.0.0.1:{Defaults.TunnelHubPort}",
                // Workstations say they are there every 10 s; one silent for 30 s is forgotten.
                Handler = new { Type = "tun", Metadata = new { Keepalive = true, Ttl = "10s" } },
                Listener = new
                {
                    Type = "tun",
                    Metadata = new
                    {
                        Name = Defaults.GatewayAdapter,
                        Net = $"{Defaults.TunnelHub}/{Defaults.TunnelNetwork.PrefixLength}",
                        Mtu = Defaults.TunnelMtu,
                        Guid = Defaults.GatewayAdapterGuid,
                    },
                },
            },
        },
    });

    public void Stop() => _gost.Stop();
}
