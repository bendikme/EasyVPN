using System.Security.Cryptography;
using System.Text.Json;

namespace EasyVPN.Gateway;

/// <summary>
/// Runs a facility's connector, <c>connectors\&lt;name&gt;.ps1</c>, with <c>Connect</c> or
/// <c>Disconnect</c>. A connector drives one kind of VPN client and gets the facility's settings
/// as environment variables, plus a private work folder. A script of the same name in the data
/// folder's connectors\ wins over the one shipped, so an adapted connector survives an update.
/// <c>Check</c> asks it, without connecting anything, whether the facility could be connected
/// from here: its VPN client installed, its settings in place.
/// What a connector's facilities share (<see cref="SharedSettings"/>, in settings.json) comes first,
/// and the facility's own settings and password over it.
/// </summary>
public sealed class Connectors(RecentLog log)
{
    // Long enough for a client that is started, signed in to and waited for (Tosi Client).
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(30);

    private readonly string _sharedPath = Path.Combine(GatewaySettings.Folder, SharedSettings.FileName);
    private readonly Lock _sync = new();
    private DateTime _sharedLoaded;
    private SharedSettings _shared = new();

    /// <param name="progress">Told each line the script prints, as it prints it.</param>
    /// <returns>The script's exit code: 0 for success.</returns>
    public async Task<int> RunAsync(Facility facility, string action, CancellationToken ct, Action<string>? progress = null)
    {
        var script = Find(facility.Connector);
        if (script is null)
        {
            log.Add($"There is no connector called '{facility.Connector}'");
            return -1;
        }

        var environment = EnvironmentOf(facility);
        if (Password(facility) is { } password)
        {
            try
            {
                environment["EASYVPN_PASSWORD"] = Secret.Reveal(password);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Not one this gateway can read. Whatever is up is still taken down, without it.
                if (action == "Connect") throw new InvalidOperationException(Unreadable(facility));
            }
        }

        // When it started, so the activity shows how long its VPN client took, and what came before.
        log.Add($"{facility.Connector}: {action} {facility.Name}");
        return await PowerShell.RunFileAsync(script, [action], environment, Limit, line =>
        {
            log.Add($"{facility.Connector}: {line}");
            progress?.Invoke(line);
        }, ct);
    }

    public async Task<CheckResult> CheckAsync(Facility facility, CancellationToken ct)
    {
        var script = Find(facility.Connector);
        if (script is null)
            return new(facility.Name, CheckLevel.Problem, $"There is no connector called '{facility.Connector}'.", FacilityId: facility.Id);
        // A connector of one's own, written before there was a Check.
        if (!(await File.ReadAllTextAsync(script, ct)).Contains("'Check'", StringComparison.Ordinal))
            return new(facility.Name, CheckLevel.Warning, $"The {facility.Connector} connector has no Check, so this cannot tell whether it is ready.", FacilityId: facility.Id);

        // The password is not handed to the check; it only has to be one this gateway can read.
        if (Password(facility) is { } password)
        {
            try
            {
                Secret.Reveal(password);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return new(facility.Name, CheckLevel.Problem, Unreadable(facility), FacilityId: facility.Id);
            }
        }

        var lines = new List<string>();
        var code = await PowerShell.RunFileAsync(script, ["Check"], EnvironmentOf(facility), CheckLimit, lines.Add, ct);
        var text = string.Join(" ", lines);
        return code == 0
            ? new(facility.Name, CheckLevel.Ok, text.Length > 0 ? text : "Ready", FacilityId: facility.Id)
            : new(facility.Name, CheckLevel.Problem, text.Length > 0 ? text : $"The {facility.Connector} connector's check failed (exit code {code}).", FacilityId: facility.Id);
    }

    /// <summary>The facility's own password, or else the one its connector's facilities share; null for none.</summary>
    private string? Password(Facility facility) =>
        string.IsNullOrEmpty(facility.Password) ? Shared(facility.Connector)?.Password is { Length: > 0 } shared ? shared : null : facility.Password;

    /// <summary>
    /// A password encrypted on another machine - facilities.json or settings.json copied from another
    /// gateway, say: Windows encrypts it for the machine, and only that machine can decrypt it.
    /// </summary>
    private static string Unreadable(Facility facility) => string.IsNullOrEmpty(facility.Password)
        ? $"The password for {facility.Connector} in the gateway's Settings was encrypted on another machine, so this gateway cannot read it. Type it again in Settings in the EasyVPN Gateway app."
        : $"{facility.Name}'s password was encrypted on another machine, so this gateway cannot read it. Type it again in the facility editor (Edit facilities in the EasyVPN Gateway app).";

    private Dictionary<string, string> EnvironmentOf(Facility facility)
    {
        var work = Directory.CreateDirectory(Path.Combine(GatewaySettings.Folder, "work", facility.Id)).FullName;
        var environment = new Dictionary<string, string>
        {
            ["EASYVPN_FACILITY"] = facility.Id,
            ["EASYVPN_WORK"] = work,
            // For a connector that needs it: EasyVPN.Gateway.exe desktop … (see DesktopSession).
            ["EASYVPN_GATEWAY"] = Environment.ProcessPath ?? "",
        };
        foreach (var settings in new[] { Shared(facility.Connector)?.Settings ?? [], facility.Settings })
            foreach (var (key, value) in settings.Where(s => s.Value.Length > 0))
                environment[$"EASYVPN_{key.ToUpperInvariant()}"] = value;
        return environment;
    }

    /// <summary>What the connector's facilities share, from settings.json as it is now.</summary>
    private ConnectorDefaults? Shared(string connector)
    {
        lock (_sync)
        {
            var stamp = File.Exists(_sharedPath) ? File.GetLastWriteTimeUtc(_sharedPath) : DateTime.MinValue;
            if (stamp != _sharedLoaded)
            {
                _sharedLoaded = stamp;
                try
                {
                    _shared = DataDirectory.ReadJson<SharedSettings>(_sharedPath) ?? new SharedSettings();
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    log.Add($"{SharedSettings.FileName} could not be read, so the last good settings stay: {ex.Message}");
                }
            }
            return _shared.For(connector);
        }
    }

    private static string? Find(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name)) return null;
        return new[] { GatewaySettings.Folder, AppContext.BaseDirectory }
            .Select(folder => Path.Combine(folder, "connectors", name + ".ps1"))
            .FirstOrDefault(File.Exists);
    }
}
