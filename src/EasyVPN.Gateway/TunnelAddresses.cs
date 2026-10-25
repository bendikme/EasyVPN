using System.Net;

namespace EasyVPN.Gateway;

/// <summary>
/// Each workstation's address on the tunnel, kept in <c>tunnel-addresses.json</c> so it gets the
/// same one each time. The hub has the first; with every other one given out, the workstation not
/// seen for longest gives its address up.
/// </summary>
public sealed class TunnelAddresses
{
    private sealed record Lease(string Client, int Host, DateTimeOffset Seen);

    private static readonly string FilePath = Path.Combine(GatewaySettings.Folder, "tunnel-addresses.json");

    private readonly Lock _sync = new();
    private readonly List<Lease> _leases = DataDirectory.ReadJson<List<Lease>>(FilePath) ?? [];

    public TunnelLease For(string client)
    {
        lock (_sync)
        {
            var lease = _leases.Find(l => string.Equals(l.Client, client, StringComparison.OrdinalIgnoreCase));
            if (lease is not null)
                _leases.Remove(lease);
            else
            {
                var taken = _leases.Select(l => l.Host).ToHashSet();
                var free = Enumerable.Range(2, Hosts - 1).Where(host => !taken.Contains(host)).Cast<int?>().FirstOrDefault();
                if (free is null)
                {
                    var oldest = _leases.MinBy(l => l.Seen)!;
                    _leases.Remove(oldest);
                    free = oldest.Host;
                }
                lease = new Lease(client, free.Value, default);
            }
            _leases.Add(lease with { Seen = DateTimeOffset.UtcNow });
            DataDirectory.WriteJson(FilePath, _leases);
            return new TunnelLease(Address(lease.Host).ToString(), Defaults.TunnelNetwork.PrefixLength);
        }
    }

    /// <summary>Host numbers 1 (the hub) to the last before the broadcast address.</summary>
    private static int Hosts => (1 << (32 - Defaults.TunnelNetwork.PrefixLength)) - 2;

    private static IPAddress Address(int host)
    {
        var bytes = Defaults.TunnelNetwork.BaseAddress.GetAddressBytes();
        var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]) + (uint)host;
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
