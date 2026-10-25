using System.Net;
using System.Text.RegularExpressions;

namespace EasyVPN.Gateway;

/// <summary>
/// <c>facilities.json</c>: the gateway reads it, and the gateway app's facility editor writes it,
/// both through these types.
/// </summary>
public sealed record FacilityFile(List<Facility> Facilities);

/// <summary>A facility as the gateway knows it: what clients see, plus how to connect to it.</summary>
public sealed partial class Facility
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Customer { get; set; } = "";

    /// <summary>The folder EasyVPN's lists show it in, nested with '/' (Tosibox/Nord). None: its customer's.</summary>
    public string? Group { get; set; }

    /// <summary>The script in connectors\ that drives the facility's VPN client: manual, windows, openvpn, wireguard, cisco …</summary>
    public string Connector { get; set; } = "manual";

    /// <summary>Handed to the connector as <c>EASYVPN_&lt;KEY&gt;</c> environment variables.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>Handed to the connector as <c>EASYVPN_PASSWORD</c>; see <see cref="Secret"/>.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// The facility's networks, as CIDR. Clients route these, and only these, to the gateway. None:
    /// the networks its VPN sets up when it connects (<see cref="VpnRoutes"/>).
    /// </summary>
    public List<string> Routes { get; set; } = [];

    /// <summary>
    /// The VPN gives the gateway the facility's Ethernet (Tosibox in Layer 2 mode, say), and that
    /// adapter is bridged with the gateway's "EasyVPN L2": workstations then get Layer 2 too,
    /// instead of routes.
    /// </summary>
    public bool Layer2 { get; set; }

    public DnsRule? Dns { get; set; }

    /// <summary>
    /// A <c>host:port</c> inside the facility that answers when it is reachable - a PLC's port 102,
    /// an HMI's 443. Without one, a connector that finishes without an error is taken at its word.
    /// </summary>
    public string? Check { get; set; }

    /// <summary>Seconds to wait for the check once the connector has run: time to sign in, for a manual one.</summary>
    public int Timeout { get; set; } = DefaultTimeout;

    public const int DefaultTimeout = 120;

    public string? Notes { get; set; }
    public List<FacilityLink> Links { get; set; } = [];

    public FacilityInfo ToInfo() => new(Id, Name, Customer, Connector, Routes, Dns, Check, Notes, Links, Layer2, Group);

    /// <summary>What keeps the gateway from using it, worded to follow its name ("… has no routes"); null when nothing does.</summary>
    public string? Problem() => this switch
    {
        _ when !IdPattern().IsMatch(Id) => "needs an id of letters, digits, '.', '-' and '_'",
        _ when Routes.FirstOrDefault(r => !IPNetwork.TryParse(r, out _)) is { } route =>
            $"has a route that is not a network in CIDR form: {route}",
        { Check: { } check } when !Probe.TrySplit(check, out _, out _) => $"has a check that is not host:port: {check}",
        _ => null,
    };

    // The id also names the connector's work folder, so it must be a plain file name.
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}
