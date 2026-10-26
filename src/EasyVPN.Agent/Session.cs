namespace EasyVPN.Agent;

/// <summary>
/// This workstation's connection.
///
/// The gateway carries one facility at a time, for every client. The session asks it for one,
/// follows what it reports, and once the gateway has connected it, carries it to this PC: a routed
/// facility's networks into the tunnel, a Layer 2 facility's Ethernet onto the "EasyVPN L2"
/// adapter. Connected here means more than the gateway saying so: this PC reached the facility.
///
/// While connected, the routes stay until the user disconnects - even when the gateway or the
/// facility stops answering - so the facility's traffic stops rather than going some other way.
/// A Layer 2 link pulls its adapter's cable out instead.
/// </summary>
public sealed class Session(Tunnel tunnel, Layer2Link layer2, RecentLog log) : BackgroundService
{
    private static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(2);
    // Asked often while connecting, so a facility is carried here as soon as the gateway has it.
    private static readonly TimeSpan ConnectingPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _wake = new(0);
    private AgentSettings _settings = AgentSettings.Load();
    private GatewayClient? _gateway;
    private TunnelLease? _lease;
    private string? _wanted;
    private Outcome _outcome = Outcome.None;
    private AgentStatus _status = new(ConnectionState.Disconnected, null, null, false, false, false, []);
    private IReadOnlyList<LogLine> _gatewayLog = [];

    // The facility carried to this PC right now, routed or linked. Only the poll loop touches it.
    private FacilityInfo? _carried;

    /// <summary>How the last connection ended: shown until the next one starts.</summary>
    private sealed record Outcome(ConnectionState State, string? FacilityId, string? Message)
    {
        public static readonly Outcome None = new(ConnectionState.Disconnected, null, null);
    }

    /// <summary>The state, and what this PC and the gateway did lately, in one list.</summary>
    public AgentStatus Status
    {
        get
        {
            lock (_sync)
            {
                var gateway = _gatewayLog.Select(line => line with { Text = $"Gateway: {line.Text}" });
                return _status with { TunnelUp = tunnel.IsUp, Log = [.. log.Snapshot().Concat(gateway).OrderBy(line => line.Time).TakeLast(60)] };
            }
        }
    }

    /// <summary>Nothing connected or being connected: an update would cut nothing off.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_sync) return _wanted is null && _carried is null;
        }
    }

    public AgentSettingsView Settings
    {
        get
        {
            lock (_sync) return _settings.View();
        }
    }

    public Task<FacilityInfo[]> FacilitiesAsync(CancellationToken ct) => Gateway().FacilitiesAsync(ct);

    public string Explain(Exception ex)
    {
        lock (_sync) return GatewayClient.Explain(ex, _settings);
    }

    public async Task ConnectAsync(string id, CancellationToken ct)
    {
        await Gateway().ConnectAsync(id, ct);
        lock (_sync)
        {
            _wanted = id;
            _outcome = Outcome.None;
            _status = _status with { State = ConnectionState.Connecting, FacilityId = id, Message = "Asking the gateway to connect" };
        }
        _wake.Release();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            _wanted = null;
            _outcome = Outcome.None;
        }
        try
        {
            await Gateway().DisconnectAsync(ct);
            log.Add("Asked the gateway to disconnect");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            log.Add($"The gateway could not be asked to disconnect: {Explain(ex)} The facility is gone from this PC; on the gateway it is still connected.");
            throw;
        }
        finally
        {
            // The facility goes from this PC even when the gateway cannot be told.
            _wake.Release();
        }
    }

    public void UpdateSettings(AgentSettingsUpdate update)
    {
        lock (_sync)
        {
            _settings = _settings.Apply(update);
            _gateway?.Dispose();
            _gateway = null;
            _lease = null;
            _wanted = null;
            _outcome = Outcome.None;
        }
        log.Add($"Now using the gateway at {update.Gateway}");
        _wake.Release();
    }

    private GatewayClient Gateway()
    {
        lock (_sync)
        {
            if (!_settings.IsConfigured) throw new InvalidOperationException("Choose a gateway in Settings first.");
            var settings = _settings;
            return _gateway ??= new GatewayClient(settings, thumbprint => Pin(settings, thumbprint));
        }
    }

    /// <summary>First contact with a gateway: remember its certificate.</summary>
    private void Pin(AgentSettings settings, string thumbprint)
    {
        lock (_sync)
        {
            if (settings != _settings) return;
            File.Delete(AgentSettings.CertificateFile);
            settings.Thumbprint = thumbprint;
            settings.Save();
        }
        log.Add($"Pinned the gateway's certificate {thumbprint}");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        LegacyGost.KillStrays();
        try
        {
            // DNS rules outlive the agent; the routes went with the old adapter.
            await tunnel.ClearAsync();
        }
        catch (Exception ex)
        {
            log.Add(ex.Message);
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.Add(ex.Message);
            }

            TimeSpan pace;
            lock (_sync) pace = _wanted is null ? IdlePoll : _carried is null ? ConnectingPoll : BusyPoll;
            try
            {
                await _wake.WaitAsync(pace, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await ReleaseAsync();
        tunnel.Stop();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        AgentSettings settings;
        string? wanted;
        Outcome outcome;
        lock (_sync) (settings, wanted, outcome) = (_settings, _wanted, _outcome);

        // Local cleanup must not depend on reaching the gateway: disconnecting or switching
        // facilities still removes the previous routes/link when the gateway is offline.
        if (_carried is not null && !string.Equals(_carried.Id, wanted, StringComparison.OrdinalIgnoreCase))
            await ReleaseAsync();

        if (!settings.IsConfigured)
        {
            await ReleaseAsync();
            tunnel.Stop();
            Publish(ConnectionState.Disconnected, null, "Choose a gateway in Settings.", configured: false, online: false);
            return;
        }

        GatewayStatus gateway;
        try
        {
            gateway = await Gateway().StatusAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException && !ct.IsCancellationRequested)
        {
            var problem = GatewayClient.Explain(ex, settings);
            if (wanted is null)
                Publish(outcome.State, outcome.FacilityId, problem, online: false);
            else
                Publish(_carried is null ? ConnectionState.Connecting : ConnectionState.Interrupted, wanted, problem, online: false);
            return;
        }

        lock (_sync) _gatewayLog = gateway.Log;
        if (await LeaseAsync(ct) is not { } lease)
        {
            await ReleaseAsync();
            Publish(ConnectionState.Disconnected, wanted, "The gateway runs an older EasyVPN than this PC. It updates itself while nothing is connected.");
            return;
        }
        tunnel.Start(Gateway(), lease);

        if (wanted is null)
        {
            await ReleaseAsync();
            var busy = gateway.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;
            Publish(outcome.State, outcome.FacilityId, outcome.Message ?? (busy ? $"The gateway is in use by {gateway.Client}" : null));
            return;
        }

        if (!string.Equals(gateway.FacilityId, wanted, StringComparison.OrdinalIgnoreCase))
        {
            await EndAsync(ConnectionState.Disconnected, wanted, gateway.FacilityId is null
                ? "Disconnected on the gateway"
                : $"{gateway.Client} switched the gateway to another facility");
            return;
        }

        switch (gateway.State)
        {
            case ConnectionState.Connecting:
            case ConnectionState.Interrupted:
                Publish(gateway.State, wanted, gateway.Message);
                break;
            case ConnectionState.Connected when _carried is null:
                await CarryAsync(settings, wanted, gateway, ct);
                break;
            case ConnectionState.Connected when _carried.Layer2:
                await FollowLinkAsync(_carried);
                break;
            case ConnectionState.Connected:
                Publish(ConnectionState.Connected, wanted, $"Connected to {_carried.Name}");
                break;
            case ConnectionState.Failed:
                await EndAsync(ConnectionState.Failed, wanted, gateway.Message);
                break;
            default:
                await EndAsync(ConnectionState.Disconnected, wanted, "Disconnected on the gateway");
                break;
        }
    }

    /// <summary>This PC's address on the tunnel, asked of the gateway once.</summary>
    private async Task<TunnelLease?> LeaseAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_lease is not null) return _lease;
        }
        var lease = await Gateway().TunnelAsync(ct);
        lock (_sync) _lease ??= lease;
        return lease;
    }

    private async Task CarryAsync(AgentSettings settings, string id, GatewayStatus gateway, CancellationToken ct)
    {
        var facility = (await Gateway().FacilitiesAsync(ct)).FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        if (facility is null)
        {
            await EndAsync(ConnectionState.Failed, id, $"The gateway no longer has the facility '{id}'");
            return;
        }
        // What the gateway carries: for a facility without routes of its own, what its VPN set up.
        if (!facility.Layer2 && gateway.Routes is { Count: > 0 } routes) facility = facility with { Routes = routes };

        _carried = facility;
        if (facility.Layer2)
        {
            layer2.Start(Gateway());
            Publish(ConnectionState.Connecting, id, "Linking at Layer 2");
            return;
        }

        Publish(ConnectionState.Connecting, id, $"Routing {facility.Name} through the tunnel");
        try
        {
            await tunnel.ApplyAsync(facility, settings.Host, ct);
        }
        catch (InvalidOperationException ex)
        {
            await EndAsync(ConnectionState.Failed, id, ex.Message);
            return;
        }

        Publish(ConnectionState.Connecting, id, "Checking the connection from this PC");
        var problem = await VerifyAsync(facility, ct);
        if (problem is not null)
        {
            await EndAsync(ConnectionState.Failed, id, problem);
            return;
        }
        log.Add($"Connected to {facility.Name}");
        Publish(ConnectionState.Connected, id, $"Connected to {facility.Name}");
    }

    /// <summary>
    /// A Layer 2 facility is connected here while its link is up. A link the network dropped is made
    /// again; one that could not start, or that another workstation took over, ends the connection.
    /// </summary>
    private async Task FollowLinkAsync(FacilityInfo facility)
    {
        if (layer2.IsUp)
        {
            var address = Layer2Link.Address();
            Publish(ConnectionState.Connected, facility.Id, address is null
                ? $"Connected to {facility.Name} at Layer 2. The \"{Defaults.Layer2Adapter}\" adapter has no address on its LAN yet."
                : $"Connected to {facility.Name} at Layer 2. The \"{Defaults.Layer2Adapter}\" adapter is {address} on its LAN.");
        }
        else if (layer2.IsRunning)
        {
            Publish(ConnectionState.Connecting, facility.Id, "Linking at Layer 2");
        }
        else if (layer2.TakenOver is { } takenOver)
        {
            await EndAsync(ConnectionState.Disconnected, facility.Id, takenOver);
        }
        else if (layer2.Failed is { } failed)
        {
            await EndAsync(ConnectionState.Failed, facility.Id, failed);
        }
        else
        {
            layer2.Start(Gateway());
            Publish(ConnectionState.Connecting, facility.Id, "Linking at Layer 2 again");
        }
    }

    /// <summary>Is the facility reachable from this PC? The relay first, then the facility through it.</summary>
    private async Task<string?> VerifyAsync(FacilityInfo facility, CancellationToken ct)
    {
        // The tunnel links up within moments of the gateway answering; a little longer after a drop.
        for (var i = 0; !tunnel.IsUp && i < 40; i++) await Task.Delay(250, ct);
        if (!tunnel.IsUp)
            return tunnel.Problem is { } why
                ? $"The tunnel to the gateway is not up. {why}"
                : "The tunnel to the gateway is not up. Check that the gateway's VPN client allows access to the local network.";
        if (facility.Check is { } check && !await Probe.TcpAsync(check, ct, timeoutMs: 5000))
            return $"{check} answers the gateway but not this PC. The activity log shows what the tunnel reported.";
        return null;
    }

    private async Task EndAsync(ConnectionState state, string facilityId, string? message)
    {
        await ReleaseAsync();
        lock (_sync)
        {
            if (_wanted == facilityId) _wanted = null;
            _outcome = new Outcome(state, facilityId, message);
        }
        if (message is not null) log.Add(message);
        Publish(state, facilityId, message);
    }

    /// <summary>Takes the carried facility off this PC: its routes and DNS rules, or its Layer 2 link.</summary>
    private async Task ReleaseAsync()
    {
        if (_carried is not { } facility) return;
        if (facility.Layer2) await layer2.StopAsync();
        else await tunnel.ClearAsync();
        _carried = null;
    }

    private void Publish(ConnectionState state, string? facilityId, string? message, bool configured = true, bool online = true)
    {
        lock (_sync) _status = new AgentStatus(state, facilityId, message, configured, online, false, []);
    }
}
