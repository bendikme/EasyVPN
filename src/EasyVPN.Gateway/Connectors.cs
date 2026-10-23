namespace EasyVPN.Gateway;

/// <summary>
/// Runs a facility's connector, <c>connectors\&lt;name&gt;.ps1</c>, with <c>Connect</c> or
/// <c>Disconnect</c>. A connector drives one kind of VPN client and gets the facility's settings
/// as environment variables, plus a private work folder. A script of the same name in the data
/// folder's connectors\ wins over the one shipped, so an adapted connector survives an update.
/// </summary>
public sealed class Connectors(RecentLog log)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    /// <returns>The script's exit code: 0 for success.</returns>
    public async Task<int> RunAsync(Facility facility, string action, CancellationToken ct)
    {
        var script = Find(facility.Connector);
        if (script is null)
        {
            log.Add($"There is no connector called '{facility.Connector}'");
            return -1;
        }

        var work = Directory.CreateDirectory(Path.Combine(GatewaySettings.Folder, "work", facility.Id)).FullName;
        var environment = new Dictionary<string, string>
        {
            ["EASYVPN_FACILITY"] = facility.Id,
            ["EASYVPN_WORK"] = work,
        };
        foreach (var (key, value) in facility.Settings)
            environment[$"EASYVPN_{key.ToUpperInvariant()}"] = value;
        if (!string.IsNullOrEmpty(facility.Password))
            environment["EASYVPN_PASSWORD"] = Secret.Reveal(facility.Password);

        return await PowerShell.RunFileAsync(script, [action], environment, Limit, line => log.Add($"{facility.Connector}: {line}"), ct);
    }

    private static string? Find(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name)) return null;
        return new[] { GatewaySettings.Folder, AppContext.BaseDirectory }
            .Select(folder => Path.Combine(folder, "connectors", name + ".ps1"))
            .FirstOrDefault(File.Exists);
    }
}
