using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyVPN.Gateway;

/// <summary>
/// The networks a facility's VPN sets up on this machine, for a facility with no routes of its own:
/// most VPN servers push their networks when the client connects, rather than writing them into the
/// profile. They are the IPv4 routes that appear while connecting, other than on the adapters the
/// gateway's own default route uses, leaving out what Windows gives every interface (its own
/// address, broadcasts, multicast) and anything wider than /8, such as a route for all traffic.
/// </summary>
public static class VpnRoutes
{
    private static readonly IPNetwork Multicast = IPNetwork.Parse("224.0.0.0/4");

    public readonly record struct Route(IPAddress Destination, int PrefixLength, IPAddress NextHop, int Interface);

    /// <summary>This machine's IPv4 routing table, as it is now.</summary>
    public static IReadOnlyList<Route> Snapshot()
    {
        const int insufficientBuffer = 122;
        const int rowSize = 14 * sizeof(uint); // MIB_IPFORWARDROW
        var size = 0;
        var result = GetIpForwardTable(IntPtr.Zero, ref size, false);
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetIpForwardTable(buffer, ref size, false);
                if (result == insufficientBuffer) continue; // It grew meanwhile; size says by how much.
                if (result != 0) throw new Win32Exception(result);

                var routes = new List<Route>();
                for (var i = 0; i < Marshal.ReadInt32(buffer); i++)
                {
                    var row = buffer + sizeof(uint) + i * rowSize;
                    var mask = (uint)Marshal.ReadInt32(row, 4);
                    routes.Add(new Route(
                        new IPAddress((uint)Marshal.ReadInt32(row)),
                        BitOperations.PopCount(mask),
                        new IPAddress((uint)Marshal.ReadInt32(row, 12)),
                        Marshal.ReadInt32(row, 16)));
                }
                return routes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The networks the VPN added between <paramref name="before"/> and <paramref name="after"/>, as CIDR.</summary>
    public static List<string> Added(IReadOnlyList<Route> before, IReadOnlyList<Route> after)
    {
        var known = before.ToHashSet();
        // Not on the adapters the gateway's own way out runs on: a route a VPN client puts there is
        // its way to its server, not into the facility. Its own adapter may well be up already - a
        // Tosi Client is online before a Lock is connected - so it is not left out for that.
        var uplinks = before.Where(r => r.PrefixLength == 0).Select(r => r.Interface).ToHashSet();
        var added = after.Where(r => !known.Contains(r) && !uplinks.Contains(r.Interface)).ToList();
        var own = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToHashSet();
        var networks = added.Where(r => r.PrefixLength < 32).Select(r => new IPNetwork(r.Destination, r.PrefixLength)).ToList();

        return
        [
            .. added
                .Where(r => r.PrefixLength >= 8)
                // The tunnel's own, should its adapter have been made again meanwhile.
                .Where(r => !Defaults.TunnelNetwork.Contains(r.Destination))
                .Where(r => !Multicast.Contains(r.Destination) && !IPAddress.IsLoopback(r.Destination) && !r.Destination.Equals(IPAddress.Broadcast))
                // A host route stays only when it is one the VPN pushed: not this machine's own
                // address, nor the broadcast address of a network beside it.
                .Where(r => r.PrefixLength < 32 || (!own.Contains(r.Destination) && !networks.Any(n => Broadcast(n).Equals(r.Destination))))
                .Select(r => new IPNetwork(r.Destination, r.PrefixLength).ToString())
                .Distinct()
                .Order(),
        ];
    }

    /// <summary>
    /// Whether every one of <paramref name="networks"/> is routed now on an adapter other than those
    /// the gateway's own default route uses: the VPN that set them up is up, whenever it came up.
    /// </summary>
    public static bool AllPresent(IReadOnlyList<string> networks, IReadOnlyList<Route> now)
    {
        if (networks.Count == 0) return false;
        var uplinks = now.Where(r => r.PrefixLength == 0).Select(r => r.Interface).ToHashSet();
        var routed = now.Where(r => !uplinks.Contains(r.Interface))
            .Select(r => new IPNetwork(r.Destination, r.PrefixLength).ToString())
            .ToHashSet();
        return networks.All(routed.Contains);
    }

    private static IPAddress Broadcast(IPNetwork network)
    {
        var bytes = network.BaseAddress.GetAddressBytes();
        var host = 32 - network.PrefixLength;
        var value = BitConverter.ToUInt32([bytes[3], bytes[2], bytes[1], bytes[0]]) | (host == 32 ? uint.MaxValue : (1u << host) - 1);
        var result = BitConverter.GetBytes(value);
        return new IPAddress([result[3], result[2], result[1], result[0]]);
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);
}
