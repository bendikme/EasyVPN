using System.Text.Json.Serialization;

namespace EasyVPN.Agent;

/// <summary>Which gateway this workstation uses, in <c>%ProgramData%\EasyVPN\Client\agent.json</c>.</summary>
public sealed class AgentSettings
{
    /// <summary>The gateway's API: <c>host</c>, or <c>host:port</c> when not on the default port.</summary>
    public string? Gateway { get; set; }

    public string? AccessKey { get; set; }

    /// <summary>SHA-256 of the gateway's certificate, pinned the first time the agent reaches it.</summary>
    public string? Thumbprint { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Gateway) && !string.IsNullOrEmpty(AccessKey);

    [JsonIgnore]
    public string Host => Probe.TrySplit(Gateway ?? "", out var host, out _) ? host : Gateway?.Trim() ?? "";

    [JsonIgnore]
    public int ApiPort => Probe.TrySplit(Gateway ?? "", out _, out var port) ? port : Defaults.GatewayApiPort;

    public static string Folder => DataDirectory.Get("Client");

    /// <summary>The pinned certificate itself, which gost on this side trusts as its only root.</summary>
    public static string CertificateFile => Path.Combine(Folder, "gateway.crt");

    private static string FilePath => Path.Combine(Folder, "agent.json");

    public static AgentSettings Load() => DataDirectory.ReadJson<AgentSettings>(FilePath) ?? new AgentSettings();

    public void Save() => DataDirectory.WriteJson(FilePath, this);

    public AgentSettingsView View() => new(Gateway, !string.IsNullOrEmpty(AccessKey), Thumbprint);
}
