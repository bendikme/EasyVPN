using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using EasyVPN;
using EasyVPN.Agent;
using EasyVPN.Gateway;

// EasyVPN.TunnelTests                  the checks that need nothing special
// EasyVPN.TunnelTests --admin          and, as administrator, those through real Wintun adapters and
//                                      Windows' own network stack: test adapters on 198.18.250-251.x,
//                                      a firewall rule letting ping in from there, all gone afterwards
// EasyVPN.TunnelTests ... --out FILE   also written to FILE (for a run in an elevated window)
var admin = args.Contains("--admin");
var outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault();
using var file = outPath is null ? null : new StreamWriter(outPath) { AutoFlush = true };
void Say(string line)
{
    Console.WriteLine(line);
    file?.WriteLine(line);
}

var passed = 0;
var failed = 0;
async Task Check(string name, Func<Task> body)
{
    var clock = Stopwatch.StartNew();
    try
    {
        await body().WaitAsync(TimeSpan.FromMinutes(2));
        passed++;
        Say($"PASS {name} ({clock.Elapsed.TotalSeconds:0.0} s)");
    }
    catch (Exception ex)
    {
        failed++;
        Say($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
    }
}
void Require(bool condition, string what)
{
    if (!condition) throw new Exception(what);
}

var log = new RecentLog(line => Say($"     log: {line}"));

// ---------------------------------------------------------------- anywhere

await Check("IPv4 reads a packet's source and destination; IPv6 and scraps are not IPv4", () =>
{
    var packet = Packets.EchoRequest(IPAddress.Parse("198.18.0.7"), IPAddress.Parse("198.18.0.1"), 1, 1, 32);
    Require(IPv4.TryRead(packet, out var source, out var destination), "an echo request was not read");
    Require(IPv4.From(source).ToString() == "198.18.0.7" && IPv4.From(destination).ToString() == "198.18.0.1", "wrong addresses");
    var six = new byte[40];
    six[0] = 0x60;
    Require(!IPv4.TryRead(six, out _, out _), "IPv6 was read as IPv4");
    Require(!IPv4.TryRead(new byte[10], out _, out _), "a scrap was read as IPv4");
    return Task.CompletedTask;
});

await Check("A packet link carries packets both ways, whole and in order", async () =>
{
    var (a, b) = await Sockets.PairAsync();
    var toA = Channel.CreateUnbounded<byte[]>();
    var toB = Channel.CreateUnbounded<byte[]>();
    var linkA = new PacketLink(a, p => toA.Writer.TryWrite(p.ToArray()));
    var linkB = new PacketLink(b, p => toB.Writer.TryWrite(p.ToArray()));
    using var stop = new CancellationTokenSource();
    var runs = Task.WhenAll(linkA.RunAsync(stop.Token), linkB.RunAsync(stop.Token));
    var sent = Enumerable.Range(0, 1000).Select(i => Packets.Filled(20 + i % 1400, (byte)i)).ToList();
    foreach (var packet in sent)
    {
        while (!linkA.Send(packet)) await Task.Delay(1);
        while (!linkB.Send(packet)) await Task.Delay(1);
    }
    foreach (var (inbox, which) in new[] { (toB, "A to B"), (toA, "B to A") })
        for (var i = 0; i < sent.Count; i++)
        {
            var got = await inbox.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Require(got.AsSpan().SequenceEqual(sent[i]), $"{which}: packet {i} came wrong");
        }
    await stop.CancelAsync();
    await runs.ContinueWith(_ => { });
});

await Check("A packet link drops, rather than waits, when its queue is full", async () =>
{
    var (a, _) = await Sockets.PairAsync();
    var link = new PacketLink(a, _ => true); // not running: nothing takes from the queue
    var queued = 0;
    while (link.Send(new byte[100]) && queued < 5000) queued++;
    Require(queued == 1024, $"{queued} queued before dropping, not 1024");
    Require(link.Dropped == 1, $"{link.Dropped} counted dropped");
});

await Check("A packet link ends when the other end closes it", async () =>
{
    var (a, b) = await Sockets.PairAsync();
    var link = new PacketLink(a, _ => true);
    var run = link.RunAsync(CancellationToken.None);
    await b.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    await run.WaitAsync(TimeSpan.FromSeconds(5));
});

await Check("The gateway closes a workstation's older link when it links again", async () =>
{
    using var hub = new Hub(log);
    var (oldClient, oldServer) = await Sockets.PairAsync();
    var (_, newServer) = await Sockets.PairAsync();
    var address = IPv4.Of(IPAddress.Parse("198.18.0.7"));
    await hub.AttachAsync(oldServer, address);
    await hub.AttachAsync(newServer, address);
    var result = await oldClient.ReceiveAsync(new byte[16], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    Require(result.MessageType == WebSocketMessageType.Close, "the older link was not closed");
    Require(hub.Links == 1, $"{hub.Links} links for one address");
});

// ---------------------------------------------------------------- as administrator

if (admin)
{
    if (!Elevated()) Say("FAIL --admin needs an elevated window: run as administrator.");
    else
    {
        using var firewall = Firewall.LetPingIn("198.18.250.0/24,198.18.251.0/24");

        await Check("Wintun makes the adapter with its address and MTU, and takes it away again", async () =>
        {
            const string name = "EasyVPN-TestAdapter";
            using (var adapter = Wintun.Create(name, "{5B4C0A8B-6F2D-4A3E-9C1B-1E2D3C4B5A60}", IPAddress.Parse("198.18.250.1"), 24, Defaults.TunnelMtu))
            {
                var nic = await Adapters.WaitAsync(name, present: true);
                var ip = nic.GetIPProperties();
                var address = ip.UnicastAddresses.FirstOrDefault(a => a.Address.ToString() == "198.18.250.1");
                Require(address is { PrefixLength: 24 }, $"its address is {string.Join(", ", ip.UnicastAddresses.Select(a => $"{a.Address}/{a.PrefixLength}"))}");
                // .NET says 65535 for any Wintun adapter; the MTU IP uses is the subinterface's.
                var mtu = Adapters.IPv4Mtu(name);
                Require(mtu == Defaults.TunnelMtu, $"its IPv4 MTU is {mtu}");
                Require(nic.OperationalStatus == OperationalStatus.Up, $"it is {nic.OperationalStatus}");
            }
            await Adapters.WaitAsync(name, present: false);
        });

        await Check("The gateway's end: Windows answers a ping sent in over a workstation's link, and drops one from another address", async () =>
        {
            using var hub = new Hub(log)
            {
                AdapterName = "EasyVPN-TestHub",
                AdapterGuid = "{5B4C0A8B-6F2D-4A3E-9C1B-1E2D3C4B5A61}",
                Network = IPNetwork.Parse("198.18.250.0/24"),
                Address = IPAddress.Parse("198.18.250.1"),
            };
            hub.Start();
            Require(hub.IsRunning, "the hub did not start");
            await Adapters.WaitAsync(hub.AdapterName, present: true);

            var workstation = IPAddress.Parse("198.18.250.7");
            var (client, server) = await Sockets.PairAsync();
            var gatewayLink = await hub.AttachAsync(server, IPv4.Of(workstation));
            var replies = Channel.CreateUnbounded<byte[]>();
            var clientLink = new PacketLink(client, p => replies.Writer.TryWrite(p.ToArray()));
            using var stop = new CancellationTokenSource();
            var runs = Task.WhenAll(gatewayLink.RunAsync(stop.Token), clientLink.RunAsync(stop.Token));

            await Ping.UntilAnsweredAsync(clientLink, replies, workstation, hub.Address, id: 7);

            // A workstation that says it is another is dropped at the gateway.
            var before = gatewayLink.Dropped;
            clientLink.Send(Packets.EchoRequest(IPAddress.Parse("198.18.250.8"), hub.Address, 8, 1, 32));
            Require(!await Ping.AnsweredAsync(replies, 8, 1, TimeSpan.FromSeconds(2)), "a ping from another address was answered");
            Require(gatewayLink.Dropped > before, "the gateway did not count it dropped");

            var short_ = 0;
            for (ushort run = 0; run < 5; run++)
            {
                var (inBefore, outBefore, droppedBefore) = (gatewayLink.Received, gatewayLink.Sent, gatewayLink.Dropped);
                var (clientOut, clientIn) = (clientLink.Sent, clientLink.Received);
                if (!await Ping.LoadAsync(clientLink, replies, workstation, hub.Address, id: (ushort)(9 + run), Say)) short_++;
                // Where any went: sent by the workstation, into Windows at the gateway, answered by Windows, back at the workstation.
                Say($"     workstation sent {clientLink.Sent - clientOut}; gateway put {gatewayLink.Received - inBefore} into Windows ({gatewayLink.Dropped - droppedBefore} dropped) and sent {gatewayLink.Sent - outBefore} answers back; workstation got {clientLink.Received - clientIn}");
            }
            Require(short_ == 0, $"{short_} of 5 loads lost more than 2%");
            await stop.CancelAsync();
            await runs.ContinueWith(_ => { });
        });

        await Check("The workstation's end: Windows answers a ping sent in over the tunnel, and the tunnel links again after a drop", async () =>
        {
            var gatewaySide = Channel.CreateUnbounded<WebSocket>();
            var tunnel = new Tunnel(log)
            {
                AdapterName = "EasyVPN-TestWorkstation",
                AdapterGuid = "{5B4C0A8B-6F2D-4A3E-9C1B-1E2D3C4B5A62}",
                Prepare = false,
            };
            var own = IPAddress.Parse("198.18.251.7");
            var gateway = IPAddress.Parse("198.18.251.1");
            tunnel.Start(async ct =>
            {
                var (client, server) = await Sockets.PairAsync();
                gatewaySide.Writer.TryWrite(server);
                return client;
            }, new TunnelLease(own.ToString(), 24), gatewaySide);
            try
            {
                for (var round = 1; round <= 2; round++)
                {
                    var socket = await gatewaySide.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
                    var replies = Channel.CreateUnbounded<byte[]>();
                    var link = new PacketLink(socket, p => replies.Writer.TryWrite(p.ToArray()));
                    using var stop = new CancellationTokenSource();
                    var run = link.RunAsync(stop.Token);
                    for (var i = 0; !tunnel.IsUp && i < 100; i++) await Task.Delay(100);
                    Require(tunnel.IsUp, $"round {round}: the tunnel did not say it was up ({tunnel.Problem})");
                    await Ping.UntilAnsweredAsync(link, replies, gateway, own, id: (ushort)(20 + round));
                    if (round == 1)
                    {
                        var short_ = 0;
                        for (ushort load = 0; load < 5; load++)
                        {
                            var (sent, got) = (link.Sent, link.Received);
                            if (!await Ping.LoadAsync(link, replies, gateway, own, id: (ushort)(30 + load), Say)) short_++;
                            Say($"     gateway side sent {link.Sent - sent}, got {link.Received - got} back from the workstation");
                        }
                        Require(short_ == 0, $"{short_} of 5 loads lost more than 2%");
                        // The gateway goes away: the tunnel links again.
                        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "gone", CancellationToken.None);
                    }
                    await stop.CancelAsync();
                    await run.ContinueWith(_ => { });
                }
            }
            finally
            {
                tunnel.Stop();
            }
            await Adapters.WaitAsync(tunnel.AdapterName, present: false);
        });
    }
}

Say($"{passed}/{passed + failed} checks passed{(admin ? "" : " (run with --admin, as administrator, for the adapter checks)")}");
return failed == 0 ? 0 : 1;

static bool Elevated() =>
    new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

/// <summary>Two ends of a WebSocket over a real loopback TCP connection.</summary>
static class Sockets
{
    public static async Task<(WebSocket Client, WebSocket Server)> PairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connecting = new TcpClient();
        var connect = connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await listener.AcceptTcpClientAsync();
        await connect;
        var client = WebSocket.CreateFromStream(connecting.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        var server = WebSocket.CreateFromStream(accepted.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        return (client, server);
    }
}

static class Packets
{
    public static byte[] Filled(int length, byte value)
    {
        var packet = new byte[length];
        Array.Fill(packet, value);
        packet[0] = 0x45;
        return packet;
    }

    /// <summary>An ICMP echo request, as ping sends it.</summary>
    public static byte[] EchoRequest(IPAddress source, IPAddress destination, ushort id, ushort sequence, int payload)
    {
        var packet = new byte[20 + 8 + payload];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), sequence);
        packet[8] = 64;  // TTL
        packet[9] = 1;   // ICMP
        source.GetAddressBytes().CopyTo(packet, 12);
        destination.GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
        var icmp = packet.AsSpan(20);
        icmp[0] = 8;     // echo request
        BinaryPrimitives.WriteUInt16BigEndian(icmp[4..], id);
        BinaryPrimitives.WriteUInt16BigEndian(icmp[6..], sequence);
        for (var i = 8; i < icmp.Length; i++) icmp[i] = (byte)i;
        BinaryPrimitives.WriteUInt16BigEndian(icmp[2..], Checksum(icmp));
        return packet;
    }

    /// <summary>An ICMP echo reply's id and sequence; false for anything else.</summary>
    public static bool IsEchoReply(byte[] packet, out ushort id, out ushort sequence)
    {
        id = sequence = 0;
        if (packet.Length < 28 || packet[0] >> 4 != 4 || packet[9] != 1) return false;
        var icmp = packet.AsSpan((packet[0] & 0x0F) * 4);
        if (icmp.Length < 8 || icmp[0] != 0) return false;
        id = BinaryPrimitives.ReadUInt16BigEndian(icmp[4..]);
        sequence = BinaryPrimitives.ReadUInt16BigEndian(icmp[6..]);
        return true;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (data.Length % 2 == 1) sum += (uint)data[^1] << 8;
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}

static class Ping
{
    /// <summary>Pings until answered: a new adapter takes Windows a moment.</summary>
    public static async Task UntilAnsweredAsync(PacketLink link, Channel<byte[]> replies, IPAddress from, IPAddress to, ushort id)
    {
        for (ushort sequence = 1; sequence <= 40; sequence++)
        {
            link.Send(Packets.EchoRequest(from, to, id, sequence, 32));
            if (await AnsweredAsync(replies, id, sequence, TimeSpan.FromMilliseconds(500))) return;
        }
        throw new Exception($"{to} did not answer a ping from {from} in 20 s");
    }

    public static async Task<bool> AnsweredAsync(Channel<byte[]> replies, ushort id, ushort sequence, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                var packet = await replies.Reader.ReadAsync(timeout.Token);
                if (Packets.IsEchoReply(packet, out var gotId, out var gotSequence) && gotId == id && gotSequence == sequence) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>2,000 pings of 1,300 bytes, 100 at a time: nearly all answered, and how fast.</summary>
    /// <returns>False when more than 2% went unanswered.</returns>
    public static async Task<bool> LoadAsync(PacketLink link, Channel<byte[]> replies, IPAddress from, IPAddress to, ushort id, Action<string> say)
    {
        const int rounds = 20, batch = 100, size = 1300;
        var answered = new HashSet<ushort>();
        var clock = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            var first = (ushort)(round * batch + 1);
            for (var i = 0; i < batch; i++) link.Send(Packets.EchoRequest(from, to, id, (ushort)(first + i), size));
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                while (answered.Count < first - 1 + batch)
                {
                    var packet = await replies.Reader.ReadAsync(wait.Token);
                    if (Packets.IsEchoReply(packet, out var gotId, out var sequence) && gotId == id) answered.Add(sequence);
                }
            }
            catch (OperationCanceledException)
            {
                // Lost in this round: counted below.
            }
        }
        var missing = Enumerable.Range(1, rounds * batch).Where(n => !answered.Contains((ushort)n)).Take(10).ToList();
        if (missing.Count > 0) say($"     not answered: {string.Join(", ", missing)}{(rounds * batch - answered.Count > 10 ? " …" : "")}");
        var megabytes = 2.0 * answered.Count * size / 1_000_000;
        say($"     {answered.Count} of {rounds * batch} pings of {size} bytes answered in {clock.Elapsed.TotalSeconds:0.00} s: {megabytes / clock.Elapsed.TotalSeconds:0.0} MB/s through the tunnel, both ways");
        return answered.Count >= rounds * batch * 0.98;
    }
}

static class Adapters
{
    /// <summary>The MTU IPv4 uses on the adapter, as netsh shows it (NlMtu); null when it is not listed.</summary>
    public static int? IPv4Mtu(string name)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            ArgumentList = { "interface", "ipv4", "show", "subinterfaces" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var netsh = Process.Start(start)!;
        var lines = netsh.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries);
        netsh.WaitForExit();
        // "      1380                1             0          6889  EasyVPN-TestAdapter"
        foreach (var line in lines)
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 5 && string.Join(' ', columns[4..]) == name && int.TryParse(columns[0], out var mtu)) return mtu;
        }
        return null;
    }

    public static async Task<NetworkInterface> WaitAsync(string name, bool present)
    {
        for (var i = 0; i < 100; i++)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            if (present && nic is { OperationalStatus: OperationalStatus.Up } && nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) return nic;
            if (!present && nic is null) return null!;
            await Task.Delay(100);
        }
        throw new Exception(present ? $"the {name} adapter did not come up" : $"the {name} adapter did not go away");
    }
}

/// <summary>Ping let in from the test networks while the checks run; the rule is taken away afterwards.</summary>
sealed class Firewall : IDisposable
{
    private const string Rule = "EasyVPN tunnel test";

    public static Firewall LetPingIn(string from)
    {
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={Rule}");
        Netsh("advfirewall", "firewall", "add", "rule", $"name={Rule}", "dir=in", "action=allow", "protocol=icmpv4:8,any", $"remoteip={from}");
        return new Firewall();
    }

    public void Dispose() => Netsh("advfirewall", "firewall", "delete", "rule", $"name={Rule}");

    private static void Netsh(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var netsh = Process.Start(start)!;
        netsh.StandardOutput.ReadToEnd();
        netsh.WaitForExit();
    }
}
