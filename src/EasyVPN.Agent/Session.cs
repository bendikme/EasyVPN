namespace EasyVPN.Agent;

/// <summary>
/// This workstation's connection.
///
/// The gateway carries one facility at a time, for every client. The session asks it for one,
/// follows what it reports, and routes the facility's networks into the tunnel once the gateway
/// has connected it. Connected here means more than the gateway saying so: a connection from this
/// PC reached the facility, through the adapter, the relay and the VPN.
///
/// While connected, the routes stay until the user disconnects - even when the gateway or the
/// facility stops answering - so the facility's traffic stops rather than going some other way.
/// </summary>
public sealed class Session(Tunnel tunnel, RecentLog log) : BackgroundService
{
    private static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _wake = new(0);
    private AgentSettings _settings = AgentSettings.Load();
    private GatewayClient? _gateway;
    private string? _wanted;
    private Outcome _outcome = Outcome.None;
    private AgentStatus _status = new(ConnectionState.Disconnected, null, null, false, false, false, []);
    private IReadOnlyList<LogLine> _gatewayLog = [];

    // The facility routed into the tunnel right now. Only the poll loop touches it.
    private FacilityInfo? _routed;

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
        }
        finally
        {
            // The routes go even when the gateway cannot be told.
            _wake.Release();
        }
    }

    public void UpdateSettings(AgentSettingsUpdate update)
    {
        lock (_sync)
        {
            var gateway = string.IsNullOrWhiteSpace(update.Gateway) ? null : update.Gateway.Trim();
            var sameGateway = string.Equals(gateway, _settings.Gateway, StringComparison.OrdinalIgnoreCase);
            _settings = new AgentSettings
            {
                Gateway = gateway,
                AccessKey = string.IsNullOrWhiteSpace(update.AccessKey) ? _settings.AccessKey : update.AccessKey.Trim(),
                // Another gateway has another certificate.
                Thumbprint = sameGateway && !update.ForgetCertificate ? _settings.Thumbprint : null,
            };
            if (_settings.Thumbprint is null) File.Delete(AgentSettings.CertificateFile);
            _settings.Save();
            _gateway?.Dispose();
            _gateway = null;
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
        GostProcess.KillStrays();
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

            bool active;
            lock (_sync) active = _wanted is not null;
            try
            {
                await _wake.WaitAsync(active ? BusyPoll : IdlePoll, stop);
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
        await UnrouteAsync();
        tunnel.Stop();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        AgentSettings settings;
        string? wanted;
        Outcome outcome;
        lock (_sync) (settings, wanted, outcome) = (_settings, _wanted, _outcome);

        if (!settings.IsConfigured)
        {
            await UnrouteAsync();
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
                Publish(_routed is null ? ConnectionState.Connecting : ConnectionState.Interrupted, wanted, problem, online: false);
            return;
        }

        lock (_sync) _gatewayLog = gateway.Log;
        tunnel.Start(settings, gateway.RelayPort);

        if (wanted is null)
        {
            await UnrouteAsync();
            var busy = gateway.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;
            Publish(outcome.State, outcome.FacilityId, outcome.Message ?? (busy ? $"The gateway is in use by {gateway.Client}" : null));
            return;
        }

        // Switched to another facility: the previous one's routes go now, not once the new one is up.
        if (_routed is not null && !string.Equals(_routed.Id, wanted, StringComparison.OrdinalIgnoreCase))
            await UnrouteAsync();

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
            case ConnectionState.Connected when _routed is null:
                await RouteAsync(settings, wanted, gateway.RelayPort, ct);
                break;
            case ConnectionState.Connected:
                Publish(ConnectionState.Connected, wanted, $"Connected to {_routed.Name}");
                break;
            case ConnectionState.Failed:
                await EndAsync(ConnectionState.Failed, wanted, gateway.Message);
                break;
            default:
                await EndAsync(ConnectionState.Disconnected, wanted, "Disconnected on the gateway");
                break;
        }
    }

    private async Task RouteAsync(AgentSettings settings, string id, int relayPort, CancellationToken ct)
    {
        var facility = (await Gateway().FacilitiesAsync(ct)).FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        if (facility is null)
        {
            await EndAsync(ConnectionState.Failed, id, $"The gateway no longer has the facility '{id}'");
            return;
        }

        Publish(ConnectionState.Connecting, id, $"Routing {facility.Name} through the tunnel");
        _routed = facility;
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
        var problem = await VerifyAsync(settings, facility, relayPort, ct);
        if (problem is not null)
        {
            await EndAsync(ConnectionState.Failed, id, problem);
            return;
        }
        log.Add($"Connected to {facility.Name}");
        Publish(ConnectionState.Connected, id, $"Connected to {facility.Name}");
    }

    /// <summary>Is the facility reachable from this PC? The relay first, then the facility through it.</summary>
    private static async Task<string?> VerifyAsync(AgentSettings settings, FacilityInfo facility, int relayPort, CancellationToken ct)
    {
        var relay = $"{settings.Host}:{relayPort}";
        if (!await Probe.TcpAsync(relay, ct))
            return $"The gateway's relay ({relay}) does not answer. Open TCP {relayPort} in the gateway's firewall, and check that its VPN client allows access to the local network.";
        if (facility.Check is { } check && !await Probe.HeldOpenAsync(check, ct))
            return $"{check} answers the gateway but not this PC. The activity log shows what the tunnel reported.";
        return null;
    }

    private async Task EndAsync(ConnectionState state, string facilityId, string? message)
    {
        await UnrouteAsync();
        lock (_sync)
        {
            if (_wanted == facilityId) _wanted = null;
            _outcome = new Outcome(state, facilityId, message);
        }
        if (message is not null) log.Add(message);
        Publish(state, facilityId, message);
    }

    private async Task UnrouteAsync()
    {
        if (_routed is null) return;
        _routed = null;
        await tunnel.ClearAsync();
    }

    private void Publish(ConnectionState state, string? facilityId, string? message, bool configured = true, bool online = true)
    {
        lock (_sync) _status = new AgentStatus(state, facilityId, message, configured, online, false, []);
    }
}
