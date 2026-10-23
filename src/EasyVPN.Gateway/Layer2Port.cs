using System.Net.WebSockets;

namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's end of a Layer 2 facility: its own TAP adapter, "EasyVPN L2", which an
/// administrator bridges once (in Network Connections) with the Layer 2 adapter of the facility's
/// VPN client - Tosibox's, say. A workstation links to it with a WebSocket on the API's port, and
/// Ethernet frames flow both ways, so the workstation's own "EasyVPN L2" adapter is on the
/// facility's LAN.
///
/// It is open only while a Layer 2 facility is connected, and serves one workstation at a time: a
/// newer one takes the link over, as a newer request takes over the gateway. Closing ends the link
/// and unplugs the adapter, so the bridge carries nothing.
/// </summary>
public sealed class Layer2Port(RecentLog log)
{
    private sealed class Link(string client, CancellationToken aborted)
    {
        public string Client { get; } = client;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(aborted);

        /// <summary>Set when another workstation took the link over.</summary>
        public string? TakenBy { get; set; }
    }

    private readonly Lock _sync = new();
    private bool _open;
    private Link? _current;

    public void Open()
    {
        lock (_sync) _open = true;
    }

    public void Close()
    {
        lock (_sync)
        {
            _open = false;
            _current?.Stop.Cancel();
        }
    }

    public async Task RunAsync(HttpContext context, string client)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var link = new Link(client, context.RequestAborted);
        lock (_sync)
        {
            if (!_open)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            if (_current is { } previous)
            {
                previous.TakenBy = client;
                previous.Stop.Cancel();
            }
            _current = link;
        }

        try
        {
            TapAdapter tap;
            try
            {
                tap = await OpenAdapterAsync(link.Stop.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                log.Add($"Layer 2: {ex.Message}");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using (tap)
            using (var socket = await context.WebSockets.AcceptWebSocketAsync())
            {
                try
                {
                    tap.SetConnected(true);
                    log.Add($"Layer 2 link to {client}");
                    await FramePump.RunAsync(tap, socket, link.Stop.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
                {
                    log.Add($"Layer 2 link to {client} failed: {ex.Message}");
                }
                await CloseAsync(socket, link);
            }
            log.Add(link.TakenBy is null ? $"Layer 2 link to {client} closed" : $"{link.TakenBy} took the Layer 2 link over from {client}");
        }
        catch (OperationCanceledException)
        {
            // Taken over, or closed, before it was up.
        }
        finally
        {
            lock (_sync)
            {
                if (_current == link) _current = null;
            }
        }
    }

    /// <summary>A link that was taken over has to wait for the old one to let go of the adapter.</summary>
    private static async Task<TapAdapter> OpenAdapterAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return TapAdapter.Open(Defaults.Layer2Adapter);
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    /// <summary>Tells the workstation when another one took over, so it does not just take it back.</summary>
    private static async Task CloseAsync(WebSocket socket, Link link)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            if (link.TakenBy is { } client)
                await socket.CloseOutputAsync(Defaults.Layer2TakenOver, $"{client} took over the Layer 2 link", CancellationToken.None);
            else
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Gone already.
        }
    }
}
