using System.Net;
using System.Text.Json;

namespace EasyVPN.Gateway;

/// <summary>
/// The facilities in <c>%ProgramData%\EasyVPN\Gateway\facilities.json</c>, read again whenever the
/// file changes. A facility with a mistake in it is left out and the mistake logged; a file that
/// does not parse at all leaves the last good list in place.
/// </summary>
public sealed class FacilityCatalog
{
    private readonly string _path = Path.Combine(GatewaySettings.Folder, "facilities.json");
    private readonly RecentLog _log;
    private readonly Lock _sync = new();
    private DateTime _loaded;
    private IReadOnlyList<Facility> _facilities = [];
    private List<string> _problems = [];

    public FacilityCatalog(RecentLog log)
    {
        _log = log;
        if (!File.Exists(_path)) DataDirectory.WriteJson(_path, new FacilityFile([]));
    }

    public string FilePath => _path;

    /// <summary>What is wrong with the file as last read: facilities left out, or why it could not be read.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            All();
            lock (_sync) return [.. _problems];
        }
    }

    public IReadOnlyList<Facility> All()
    {
        lock (_sync)
        {
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loaded) return _facilities;
            _loaded = stamp;
            _problems = [];
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
                _problems.Add($"facilities.json could not be read, so the last good list stays: {ex.Message}");
                _log.Add(_problems[^1]);
            }
            return _facilities;
        }
    }

    public Facility? Find(string id) =>
        All().FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private bool IsValid(Facility facility)
    {
        if (facility.Problem() is not { } problem) return true;
        _problems.Add($"Facility '{facility.Name}' ({facility.Id}) {problem}, so it is left out");
        _log.Add(_problems[^1]);
        return false;
    }
}
