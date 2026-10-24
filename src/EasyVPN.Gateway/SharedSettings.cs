namespace EasyVPN.Gateway;

/// <summary>
/// <c>settings.json</c> beside facilities.json: what a connector's facilities share, set once in
/// the gateway app's Settings rather than in each of them - Tosi Client's program and password, for
/// one. A facility's own setting, or its own password, wins over these. The gateway reads it again
/// whenever it changes; the gateway app's Settings window writes it.
/// </summary>
public sealed class SharedSettings
{
    public const string FileName = "settings.json";

    /// <summary>By connector name.</summary>
    public Dictionary<string, ConnectorDefaults> Connectors { get; set; } = [];

    public ConnectorDefaults? For(string connector) =>
        Connectors.FirstOrDefault(c => string.Equals(c.Key, connector, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A connector's settings and password for every facility that uses it.</summary>
public sealed class ConnectorDefaults
{
    /// <summary>Handed to the connector as <c>EASYVPN_&lt;KEY&gt;</c>, unless the facility has its own.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>The facility's password when it has none; see <see cref="Secret"/>.</summary>
    public string? Password { get; set; }
}
