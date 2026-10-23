namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's end of the tunnel: gost relaying clients' TCP and UDP, over TLS, as connections
/// of its own - which go wherever this machine's routes send them, so through whichever VPN is up.
/// It is open only while a facility is connected, and then only to that facility's networks, so
/// nothing a client sends can reach anything else. Closing it drops every connection it carries.
/// </summary>
public sealed class Relay(GatewaySettings settings, GatewayCertificate certificate, RecentLog log)
{
    private readonly GostProcess _gost = new(Path.Combine(GatewaySettings.Folder, "relay.json"), log.Add);

    public void Open(Facility facility) => _gost.Start(new
    {
        Log = new { Level = "warn" },
        Services = new[]
        {
            new
            {
                Name = "relay",
                Addr = $"{settings.ListenAddress}:{settings.RelayPort}",
                Bypass = "facility",
                Handler = new { Type = "relay", Auth = new { Username = Defaults.RelayUser, Password = settings.AccessKey } },
                Listener = new { Type = "tls", Tls = new { certificate.CertFile, certificate.KeyFile } },
            },
        },
        Bypasses = new[]
        {
            new { Name = "facility", Whitelist = true, Matchers = facility.Routes.Concat(facility.Dns?.Servers ?? []).Distinct() },
        },
    });

    public void Close() => _gost.Stop();
}
