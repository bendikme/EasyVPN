using System.Net.WebSockets;

namespace EasyVPN;

/// <summary>
/// Carries Ethernet frames between a TAP adapter and a WebSocket, one frame per message, both ways,
/// until either side stops. Both ends of a Layer 2 link run the same pump.
/// </summary>
public static class FramePump
{
    /// <summary>Returns when the other end closes; throws when the link fails.</summary>
    public static async Task RunAsync(TapAdapter tap, WebSocket socket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outbound = Task.Run(() => FromAdapterAsync(tap, socket, stop.Token), CancellationToken.None);
        var inbound = Task.Run(() => ToAdapterAsync(socket, tap, stop.Token), CancellationToken.None);

        var first = await Task.WhenAny(outbound, inbound);
        await stop.CancelAsync();
        try
        {
            await Task.WhenAll(outbound, inbound);
        }
        catch (Exception)
        {
            // The other direction only ends by being cancelled; the one that stopped first says why.
        }
        if (!ct.IsCancellationRequested) await first;
    }

    private static async Task FromAdapterAsync(TapAdapter tap, WebSocket socket, CancellationToken ct)
    {
        var frame = new byte[TapAdapter.MaxFrame];
        while (true)
        {
            var length = await tap.ReadAsync(frame, ct);
            await socket.SendAsync(frame.AsMemory(0, length), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    private static async Task ToAdapterAsync(WebSocket socket, TapAdapter tap, CancellationToken ct)
    {
        var frame = new byte[TapAdapter.MaxFrame];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == frame.Length) throw new InvalidDataException("The other end sent something too large to be an Ethernet frame.");
                result = await socket.ReceiveAsync(frame.AsMemory(length), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                length += result.Count;
            }
            while (!result.EndOfMessage);
            await tap.WriteAsync(frame.AsMemory(0, length), ct);
        }
    }
}
