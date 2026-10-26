using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyVPN;

/// <summary>Where a facility connection is. The gateway and the agent report the same states.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>Was connected, but the facility stopped answering. Its traffic is blocked, not rerouted.</summary>
    Interrupted,
    Disconnecting,
    Failed,
}

/// <summary>
/// A facility as clients see it: everything except how the gateway connects to it. A routed
/// facility's <see cref="Routes"/> go through the tunnel; a <see cref="Layer2"/> facility's
/// Ethernet reaches the workstation's "EasyVPN L2" adapter instead. <see cref="Group"/> is the
/// folder the lists show it in, nested with '/' (Tosibox/Nord); without one, its customer's.
/// </summary>
public sealed record FacilityInfo(
    string Id,
    string Name,
    string Customer,
    string Connector,
    IReadOnlyList<string> Routes,
    DnsRule? Dns,
    string? Check,
    string? Notes,
    IReadOnlyList<FacilityLink> Links,
    bool Layer2 = false,
    string? Group = null);

/// <summary>Names under <see cref="Domains"/> resolve through <see cref="Servers"/> while the facility is connected.</summary>
public sealed record DnsRule(IReadOnlyList<string> Domains, IReadOnlyList<string> Servers);

/// <summary>Something to open once connected: a web page, or <c>rdp://host</c> for Remote Desktop.</summary>
public sealed record FacilityLink(string Name, string Url);

public sealed record LogLine(DateTimeOffset Time, string Text);

/// <summary>
/// <see cref="Routes"/>: while a routed facility is connected, the networks the gateway carries to
/// it - its own routes, or for one without any, those its VPN set up. Clients route these.
/// </summary>
public sealed record GatewayStatus(
    ConnectionState State,
    string? FacilityId,
    string? Message,
    string? Client,
    // gost's relay port, up to 0.4; 0 since the tunnel runs on the API port.
    int RelayPort,
    IReadOnlyList<LogLine> Log,
    IReadOnlyList<string>? Routes = null);

/// <summary>
/// The gateway as the EasyVPN Gateway app on its desktop shows it: the connection, what
/// workstations need to reach it, and the facilities. The access key is not here: it takes an
/// administrator to see it.
/// </summary>
public sealed record GatewayOverview(
    GatewayStatus Status,
    string Machine,
    IReadOnlyList<string> Addresses,
    int ApiPort,
    string Thumbprint,
    IReadOnlyList<FacilityInfo> Facilities,
    IReadOnlyList<string> FacilityProblems,
    string FacilitiesFile,
    UpdateStatus? Update = null);

public enum UpdateState
{
    /// <summary>Not looked yet, or a development build that does not update itself.</summary>
    Unknown,
    Checking,
    UpToDate,
    Available,
    Downloading,
    /// <summary>The installer is about to run: the apps start a watcher that brings them back, and exit.</summary>
    Installing,
    Failed,
}

/// <summary>
/// Where a service is with updating itself from the GitHub releases. <see cref="Automatic"/>: it
/// installs a newer version by itself when nothing is connected.
/// </summary>
public sealed record UpdateStatus(
    string Current,
    UpdateState State,
    string? Latest = null,
    string? Message = null,
    DateTimeOffset? Checked = null,
    bool Automatic = true);

public enum CheckLevel
{
    Ok,
    Warning,
    Problem,
}

/// <summary>
/// One thing the gateway needs, and whether it has it. <see cref="Fix"/> names what the app can
/// do about it: one of <see cref="GatewayFix"/>, or a web page. A facility's own check (its
/// connector's) carries its <see cref="FacilityId"/>.
/// </summary>
public sealed record CheckResult(string Subject, CheckLevel Level, string Text, string? Fix = null, string? FacilityId = null);

/// <summary>What the gateway knows of its checks, and whether it is checking now (in the background).</summary>
public sealed record GatewayCheckRun(IReadOnlyList<CheckResult> Results, bool Running);

public static class GatewayFix
{
    /// <summary>The gateway service adds the "EasyVPN L2" adapter.</summary>
    public const string Layer2Adapter = "layer2-adapter";

    /// <summary>Network Connections, to bridge "EasyVPN L2" with the VPN's Layer 2 adapter.</summary>
    public const string Bridge = "bridge";

    /// <summary>Where OpenVPN, and with it the TAP-Windows6 driver, comes from.</summary>
    public const string OpenVpnDownload = "https://openvpn.net/community-downloads/";
}

public sealed record AgentStatus(
    ConnectionState State,
    string? FacilityId,
    string? Message,
    bool Configured,
    bool GatewayOnline,
    bool TunnelUp,
    IReadOnlyList<LogLine> Log,
    UpdateStatus? Update = null);

/// <summary>A workstation's address on the tunnel, which the gateway keeps for it: the same one each time.</summary>
public sealed record TunnelLease(string Address, int PrefixLength);

public sealed record AgentSettingsView(string? Gateway, bool HasAccessKey, string? Thumbprint);

/// <summary>A null or empty access key keeps the current one.</summary>
public sealed record AgentSettingsUpdate(string? Gateway, string? AccessKey, bool ForgetCertificate);

public static class Defaults
{
    /// <summary>The gateway's API, and the tunnel on it.</summary>
    public const int GatewayApiPort = 8420;
    public const string AgentUrl = "http://127.0.0.1:8429";

    /// <summary>
    /// The gateway's own desktop talks to it here: plain HTTP on 127.0.0.1 only, guarded as the
    /// agent's API is. Nothing on it reveals the access key.
    /// </summary>
    public const int GatewayLocalPort = 8428;

    /// <summary>The name in the gateway's self-signed certificate. Clients pin the certificate, not the name.</summary>
    public const string CertificateName = "easyvpn-gateway";

    /// <summary>
    /// The tunnel's own network, from the benchmarking range no facility should be using. The
    /// gateway's end has <see cref="TunnelHub"/>; each workstation gets an address of its own.
    /// </summary>
    public static readonly System.Net.IPNetwork TunnelNetwork = System.Net.IPNetwork.Parse("198.18.0.0/24");

    public const string TunnelHub = "198.18.0.1";

    /// <summary>Room for the TLS, TCP and WebSocket the packets travel in, and for the facility's VPN after that.</summary>
    public const int TunnelMtu = 1380;

    /// <summary>
    /// The workstation's adapter, and the fixed identity it is made with each time: with a new one,
    /// Windows takes it for a network it has not seen and numbers its name ("EasyVPN 12").
    /// </summary>
    public const string TunnelAdapter = "EasyVPN";
    public const string TunnelAdapterGuid = "{EE9A2589-4133-47FA-9858-85601C3D689D}";

    /// <summary>The gateway's adapter, where the workstations' packets come out. Named so since 0.4, which Windows remembers.</summary>
    public const string GatewayAdapter = "EasyVPN-Gateway";
    public const string GatewayAdapterGuid = "{C6D4DDD3-E5F6-4BD5-BCA7-B8812077DA93}";

    /// <summary>The TAP adapter that carries a Layer 2 facility, on the workstation and on the gateway.</summary>
    public const string Layer2Adapter = "EasyVPN L2";

    /// <summary>A gateway closes a Layer 2 link with this status when another workstation takes it over.</summary>
    public const System.Net.WebSockets.WebSocketCloseStatus Layer2TakenOver = System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation;

    /// <summary>The machine a gateway request comes from, so everyone can see who switched the gateway.</summary>
    public const string ClientHeader = "X-EasyVPN-Client";

    /// <summary>
    /// Sent by the app on every call to the agent. A web page cannot add a header to a cross-site
    /// request without the browser asking first, so this keeps pages in the browser off the agent.
    /// </summary>
    public const string AgentHeader = "X-EasyVPN";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
