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

    public static bool TrySplit(string endpoint, out string host, out int port)
    {
        var colon = endpoint.LastIndexOf(':');
        host = colon > 0 ? endpoint[..colon] : "";
        port = 0;
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out port) && port is > 0 and < 65536;
    }
}
