using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EasyVPN.Gateway;

/// <summary>
/// The connected facility's VPN, shared with the workstations by Windows' Internet Connection
/// Sharing: their packets, out of the hub's adapter, go into the VPN's adapter as if this machine
/// sent them, and the answers come back. So a workstation's EasyVPN adapter works as the VPN
/// client's own adapter would there - ping, TCP, UDP - whichever VPN client made it. sharing.ps1,
/// beside the exe, does it, as SYSTEM.
///
/// The facility only knows this machine, so it cannot open connections to a workstation.
/// </summary>
public sealed class Sharing(RecentLog log)
{
    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "sharing.ps1");

    // Sharing takes Windows a few seconds. One that takes far longer has hung - Windows' sharing
    // service stuck, or Routing and Remote Access holding it - and waiting on is no use: everything
    // else the gateway is asked waits with it.
    private static readonly TimeSpan ShareLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnshareLimit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The adapter this machine reaches the facility's networks through now, by its own routes:
    /// the VPN's. Null when none of them leads anywhere but back into the tunnel.
    /// </summary>
    public static NetworkInterface? Carrying(IReadOnlyList<string> routes)
    {
        var indexes = new List<int>();
        foreach (var route in routes)
        {
            if (!IPNetwork.TryParse(route, out var network) || network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                continue;
            if (GetBestInterface(BitConverter.ToUInt32(network.BaseAddress.GetAddressBytes()), out var index) == 0)
                indexes.Add((int)index);
        }

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && nic.Name != Defaults.GatewayAdapter)
            .Select(nic => (Nic: nic, Index: Index(nic)))
            .Where(a => a.Index is not null)
            .ToList();
        // Most of the networks, should a VPN split them over adapters.
        return indexes
            .GroupBy(index => index)
            .OrderByDescending(group => group.Count())
            .Select(group => adapters.FirstOrDefault(a => a.Index == group.Key).Nic)
            .FirstOrDefault(nic => nic is not null);
    }

    /// <exception cref="InvalidOperationException">Windows would not share it: what it said.</exception>
    public async Task ShareAsync(NetworkInterface vpn, CancellationToken ct)
    {
        var (code, lines) = await RunAsync("Share", new Dictionary<string, string>
        {
            ["EASYVPN_VPN_ID"] = vpn.Id,
            ["EASYVPN_VPN_NAME"] = vpn.Name,
            ["EASYVPN_VPN_DEVICE"] = vpn.Description,
        }, ShareLimit, ct);
        if (code != 0) throw new InvalidOperationException($"Could not share {vpn.Name} with the workstations: {lines.LastOrDefault()}");
        lines.ForEach(log.Add);
    }

    /// <summary>Stops sharing, if the gateway was. A failure is logged: there is nothing more to do about it.</summary>
    public async Task UnshareAsync()
    {
        var (code, lines) = await RunAsync("Unshare", [], UnshareLimit, CancellationToken.None);
        if (code != 0) log.Add($"Could not stop sharing the VPN with the workstations: {lines.LastOrDefault()}");
        else lines.ForEach(log.Add);
    }

    /// <summary>Whether sharing can be used here and what is shared now (empty for nothing), or the problem.</summary>
    public async Task<(bool Ok, string Text)> StatusAsync(CancellationToken ct)
    {
        var (code, lines) = await RunAsync("Status", [], UnshareLimit, ct);
        if (code != 0) return (false, lines.LastOrDefault() ?? "sharing.ps1 failed");
        var shared = lines.FirstOrDefault(l => l.StartsWith("shared=", StringComparison.Ordinal))?["shared=".Length..];
        return (true, shared ?? "");
    }

    private static async Task<(int Code, List<string> Lines)> RunAsync(string action, Dictionary<string, string> environment, TimeSpan limit, CancellationToken ct)
    {
        environment["EASYVPN_HUB"] = Defaults.GatewayAdapterGuid;
        environment["EASYVPN_HUB_NAME"] = Defaults.GatewayAdapter;
        environment["EASYVPN_HUB_ADDRESS"] = Defaults.TunnelHub;
        var lines = new List<string>();
        var code = await PowerShell.RunFileAsync(Script, [action], environment, limit, lines.Add, ct);
        return (code, lines);
    }

    private static int? Index(NetworkInterface nic)
    {
        try
        {
            return nic.Supports(NetworkInterfaceComponent.IPv4) ? nic.GetIPProperties().GetIPv4Properties()?.Index : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint index);
}
