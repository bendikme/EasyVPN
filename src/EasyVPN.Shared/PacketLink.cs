using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace EasyVPN;

/// <summary>
/// IP packets over a WebSocket, one packet a message, both ways: the tunnel between a workstation
/// and the gateway, on the gateway's API port, with its access key and pinned certificate. Sending
/// never waits on the network: packets queue, and when the queue is full the newest are dropped,
/// as a router drops what it cannot send on - TCP sends them again.
/// </summary>
public sealed class PacketLink(WebSocket socket, PacketLink.Deliver deliver)
{
    /// <summary>Takes a packet that came over the link; false when it was dropped.</summary>
    public delegate bool Deliver(ReadOnlySpan<byte> packet);

    private const int Queue = 1024;

    // Wait, not one of the Drop modes: those take every write and throw it away unseen, and here
    // TryWrite must say so, to count it and give its buffer back. TryWrite itself never waits.
    private readonly Channel<(byte[] Buffer, int Length)> _out = Channel.CreateBounded<(byte[], int)>(
        new BoundedChannelOptions(Queue) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private long _sent, _received, _dropped;

    public WebSocket Socket => socket;

    public long Sent => Interlocked.Read(ref _sent);
    public long Received => Interlocked.Read(ref _received);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues a packet to go over the link; false when the queue was full and it was dropped.</summary>
    public bool Send(ReadOnlySpan<byte> packet)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(packet.Length);
        packet.CopyTo(buffer);
        if (_out.Writer.TryWrite((buffer, packet.Length))) return true;
        ArrayPool<byte>.Shared.Return(buffer);
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Carries packets until the other end closes; throws when the link fails.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = Task.Run(() => SendAsync(stop.Token), CancellationToken.None);
        var receiving = Task.Run(() => ReceiveAsync(stop.Token), CancellationToken.None);

        var first = await Task.WhenAny(sending, receiving);
        await stop.CancelAsync();
        _out.Writer.TryComplete();
        try
        {
            await Task.WhenAll(sending, receiving);
        }
        catch (Exception)
        {
            // The other direction only ends by being cancelled; the one that stopped first says why.
        }
        while (_out.Reader.TryRead(out var left)) ArrayPool<byte>.Shared.Return(left.Buffer);
        if (!ct.IsCancellationRequested) await first;
    }

    private async Task SendAsync(CancellationToken ct)
    {
        await foreach (var (buffer, length) in _out.Reader.ReadAllAsync(ct))
        {
            try
            {
                await socket.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, endOfMessage: true, ct);
                Interlocked.Increment(ref _sent);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var packet = new byte[Wintun.MaxPacket];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == packet.Length) throw new InvalidDataException("The other end sent something too large to be an IP packet.");
                result = await socket.ReceiveAsync(packet.AsMemory(length), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                length += result.Count;
            }
            while (!result.EndOfMessage);
            if (deliver(packet.AsSpan(0, length))) Interlocked.Increment(ref _received);
            else Interlocked.Increment(ref _dropped);
        }
    }
}

/// <summary>What the tunnel reads of an IP packet: where it is from and where it goes.</summary>
public static class IPv4
{
    /// <summary>An IPv4 packet's source and destination; false for anything else (IPv6, a scrap).</summary>
    public static bool TryRead(ReadOnlySpan<byte> packet, out uint source, out uint destination)
    {
        source = destination = 0;
        if (packet.Length < 20 || packet[0] >> 4 != 4) return false;
        source = BinaryPrimitives.ReadUInt32BigEndian(packet[12..]);
        destination = BinaryPrimitives.ReadUInt32BigEndian(packet[16..]);
        return true;
    }

    public static uint Of(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    public static IPAddress From(uint address)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }
}
