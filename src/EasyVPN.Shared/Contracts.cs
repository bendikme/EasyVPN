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
/// Ethernet reaches the workstation's "EasyVPN L2" adapter instead.
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
    bool Layer2 = false);

/// <summary>Names under <see cref="Domains"/> resolve through <see cref="Servers"/> while the facility is connected.</summary>
public sealed record DnsRule(IReadOnlyList<string> Domains, IReadOnlyList<string> Servers);

/// <summary>Something to open once connected: a web page, or <c>rdp://host</c> for Remote Desktop.</summary>
public sealed record FacilityLink(string Name, string Url);

public sealed record LogLine(DateTimeOffset Time, string Text);

public sealed record GatewayStatus(
    ConnectionState State,
    string? FacilityId,
    string? Message,
    string? Client,
    int RelayPort,
    IReadOnlyList<LogLine> Log);

public sealed record AgentStatus(
    ConnectionState State,
    string? FacilityId,
    string? Message,
    bool Configured,
    bool GatewayOnline,
    bool TunnelUp,
    IReadOnlyList<LogLine> Log);

public sealed record AgentSettingsView(string? Gateway, bool HasAccessKey, string? Thumbprint);

/// <summary>A null or empty access key keeps the current one.</summary>
public sealed record AgentSettingsUpdate(string? Gateway, string? AccessKey, bool ForgetCertificate);

public static class Defaults
{
    public const int GatewayApiPort = 8420;
    public const int GatewayRelayPort = 8421;
    public const string AgentUrl = "http://127.0.0.1:8429";

    /// <summary>The name in the gateway's self-signed certificate. Clients pin the certificate, not the name.</summary>
    public const string CertificateName = "easyvpn-gateway";

    public const string RelayUser = "easyvpn";

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
