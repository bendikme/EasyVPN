using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace EasyVPN.Agent;

/// <summary>
/// The workstation's end of a Layer 2 facility: the "EasyVPN L2" TAP adapter, linked to the
/// gateway's Layer 2 port. While the link is up the adapter's cable is plugged in and it sits on
/// the facility's LAN - DHCP, broadcasts, PROFINET discovery and all. When the link ends the cable
/// is pulled out, so nothing meant for the facility goes anywhere else.
/// </summary>
public sealed class Layer2Link(RecentLog log)
{
    private Task? _run;
    private CancellationTokenSource? _stop;
    private volatile bool _up;

    public bool IsRunning => _run is { IsCompleted: false };

    public bool IsUp => _up && IsRunning;

    /// <summary>Set when another workstation took the link over: not to be taken back.</summary>
    public string? TakenOver { get; private set; }

    /// <summary>Set when the link could not be made or used, for a reason trying again will not fix.</summary>
    public string? Failed { get; private set; }

    public void Start(GatewayClient gateway)
    {
        if (IsRunning) return;
        TakenOver = null;
        Failed = null;
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _run = Task.Run(() => RunAsync(gateway, ct), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_run is null) return;
        await _stop!.CancelAsync();
        await _run;
        _run = null;
    }

    /// <summary>The adapter's IPv4 address on the facility's LAN, once it has one.</summary>
    public static string? Address() =>
        TapAdapter.Find(Defaults.Layer2Adapter)?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && a.PrefixOrigin != PrefixOrigin.WellKnown
                && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            ?.Address.ToString();

    private async Task RunAsync(GatewayClient gateway, CancellationToken ct)
    {
        try
        {
            // Made the first time a Layer 2 facility is used, when OpenVPN's TAP driver is there.
            await Layer2Setup.EnsureAdapterAsync(workstation: true, log.Add, ct);
            using var tap = TapAdapter.Open(Defaults.Layer2Adapter);
            using var socket = await gateway.OpenLayer2Async(ct);
            tap.SetConnected(true);
            _up = true;
            log.Add($"Layer 2 link up on the \"{Defaults.Layer2Adapter}\" adapter");
            await FramePump.RunAsync(tap, socket, ct);
            if (socket.CloseStatus == Defaults.Layer2TakenOver) TakenOver = socket.CloseStatusDescription ?? "Another PC took over the Layer 2 link";
            log.Add(TakenOver ?? "Layer 2 link closed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped here.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Nothing to retry: no adapter, the adapter in use, the port closed.
            Failed = ex.Message;
            log.Add($"Layer 2 link: {ex.Message}");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or InvalidDataException)
        {
            // The network: worth trying again while the facility is up.
            log.Add($"Layer 2 link lost: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Anything else would only fail the same way again.
            Failed = ex.Message;
            log.Add($"Layer 2 link: {ex.Message}");
        }
        finally
        {
            _up = false;
        }
    }
}
