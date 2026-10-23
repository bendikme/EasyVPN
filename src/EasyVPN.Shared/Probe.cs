using System.Net.Sockets;

namespace EasyVPN;

/// <summary>Is something answering at <c>host:port</c>?</summary>
public static class Probe
{
    public static async Task<bool> TcpAsync(string endpoint, CancellationToken ct, int timeoutMs = 3000)
    {
        if (!TrySplit(endpoint, out var host, out var port)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// The same question asked through the tunnel. tun2socks completes the TCP handshake locally,
    /// before the gateway has even tried the facility, so a connection alone proves nothing. What
    /// does: a connection the gateway could not complete is closed straight away, and one it could
    /// stays open. Holding it for a moment tells the two apart.
    /// </summary>
    public static async Task<bool> HeldOpenAsync(string endpoint, CancellationToken ct, int holdMs = 3000)
    {
        if (!TrySplit(endpoint, out var host, out var port)) return false;
        using var tcp = new TcpClient();
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(5000);
                await tcp.ConnectAsync(host, port, connect.Token);
            }

            using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hold.CancelAfter(holdMs);
            try
            {
                return await tcp.GetStream().ReadAsync(new byte[1], hold.Token) > 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return true;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public static bool TrySplit(string endpoint, out string host, out int port)
    {
        var colon = endpoint.LastIndexOf(':');
        host = colon > 0 ? endpoint[..colon] : "";
        port = 0;
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out port) && port is > 0 and < 65536;
    }
}
