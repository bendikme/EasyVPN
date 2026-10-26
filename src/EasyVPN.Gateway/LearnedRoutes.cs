using System.Text.Json;

namespace EasyVPN.Gateway;

/// <summary>
/// The networks each facility's VPN set up the last time it connected, in learned-routes.json, for
/// a facility without routes of its own. A VPN found up already - a Tosibox Lock someone connected in
/// Tosi Client - sets up nothing new to learn them from; and a VPN that sets up what it did before
/// is known to be done at once, without waiting for its networks to hold still.
/// </summary>
public sealed class LearnedRoutes
{
    private readonly string _path = Path.Combine(GatewaySettings.Folder, "learned-routes.json");
    private readonly Lock _sync = new();

    public IReadOnlyList<string> Of(string facilityId)
    {
        lock (_sync) return Read().GetValueOrDefault(facilityId.ToLowerInvariant()) ?? [];
    }

    public void Remember(string facilityId, IReadOnlyList<string> routes)
    {
        lock (_sync)
        {
            var all = Read();
            all[facilityId.ToLowerInvariant()] = [.. routes];
            try
            {
                DataDirectory.WriteJson(_path, all);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Learned again next time.
            }
        }
    }

    private Dictionary<string, List<string>> Read()
    {
        try
        {
            return DataDirectory.ReadJson<Dictionary<string, List<string>>>(_path) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
