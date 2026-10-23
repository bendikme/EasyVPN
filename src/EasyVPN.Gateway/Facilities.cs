using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyVPN.Gateway;

/// <summary>A facility as the gateway knows it: what clients see, plus how to connect to it.</summary>
public sealed class Facility
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Customer { get; set; } = "";

    /// <summary>The script in connectors\ that drives the facility's VPN client: manual, windows, openvpn, wireguard, cisco …</summary>
    public string Connector { get; set; } = "manual";

    /// <summary>Handed to the connector as <c>EASYVPN_&lt;KEY&gt;</c> environment variables.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>Handed to the connector as <c>EASYVPN_PASSWORD</c>; see <see cref="Secret"/>.</summary>
    public string? Password { get; set; }

    /// <summary>The facility's networks, as CIDR. Clients route these, and only these, to the gateway.</summary>
    public List<string> Routes { get; set; } = [];

    public DnsRule? Dns { get; set; }

    /// <summary>
    /// A <c>host:port</c> inside the facility that answers when it is reachable - a PLC's port 102,
    /// an HMI's 443. Without one, a connector that finishes without an error is taken at its word.
    /// </summary>
    public string? Check { get; set; }

    /// <summary>Seconds to wait for the check once the connector has run: time to sign in, for a manual one.</summary>
    public int Timeout { get; set; } = 120;

    public string? Notes { get; set; }
    public List<FacilityLink> Links { get; set; } = [];

    public FacilityInfo ToInfo() => new(Id, Name, Customer, Connector, Routes, Dns, Check, Notes, Links);
}

/// <summary>
/// The facilities in <c>%ProgramData%\EasyVPN\Gateway\facilities.json</c>, read again whenever the
/// file changes. A facility with a mistake in it is left out and the mistake logged; a file that
/// does not parse at all leaves the last good list in place.
/// </summary>
public sealed partial class FacilityCatalog
{
    private sealed record FacilityFile(List<Facility> Facilities);

    private readonly string _path = Path.Combine(GatewaySettings.Folder, "facilities.json");
    private readonly RecentLog _log;
    private readonly Lock _sync = new();
    private DateTime _loaded;
    private IReadOnlyList<Facility> _facilities = [];

    public FacilityCatalog(RecentLog log)
    {
        _log = log;
        if (!File.Exists(_path)) DataDirectory.WriteJson(_path, new FacilityFile([]));
    }

    public IReadOnlyList<Facility> All()
    {
        lock (_sync)
        {
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loaded) return _facilities;
            _loaded = stamp;
            try
            {
                var file = DataDirectory.ReadJson<FacilityFile>(_path);
                _facilities = [.. (file?.Facilities ?? []).Where(IsValid)];
                // 10.60.0.7/24 reads as 10.60.0.0/24; clients and the relay get the network itself.
                foreach (var facility in _facilities)
                    facility.Routes = [.. facility.Routes.Select(r => IPNetwork.Parse(r).ToString())];
                _log.Add($"Read {_facilities.Count} facilities from facilities.json");
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _log.Add($"facilities.json could not be read, so the last good list stays: {ex.Message}");
            }
            return _facilities;
        }
    }

    public Facility? Find(string id) =>
        All().FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private bool IsValid(Facility facility)
    {
        var problem = facility switch
        {
            _ when !IdPattern().IsMatch(facility.Id) => "needs an id of letters, digits, '.', '-' and '_'",
            { Routes.Count: 0 } => "has no routes",
            _ when facility.Routes.FirstOrDefault(r => !IPNetwork.TryParse(r, out _)) is { } route =>
                $"has a route that is not a network in CIDR form: {route}",
            { Check: { } check } when !Probe.TrySplit(check, out _, out _) => $"has a check that is not host:port: {check}",
            _ => null,
        };
        if (problem is not null) _log.Add($"Facility '{facility.Name}' ({facility.Id}) {problem}, so it is left out");
        return problem is null;
    }

    // The id also names the connector's work folder, so it must be a plain file name.
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}
