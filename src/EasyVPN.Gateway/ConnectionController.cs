namespace EasyVPN.Gateway;

/// <summary>
/// The one facility connection the gateway carries, shared by every client.
///
/// Connecting first takes down whatever was up, then runs the facility's connector, waits until
/// the facility answers from here, and only then opens the relay to it: Connected means the
/// facility was reached, not that a VPN client turned green. Each request supersedes the one
/// before - an attempt still in progress is cancelled, and only the newest request is carried out.
///
/// Taking a facility down is only done once its check has stopped answering. Two facilities often
/// use the same addresses, and a VPN left up underneath would quietly take the next one's traffic.
///
/// While connected the facility is checked every few seconds. When it stops answering, the relay
/// closes until it answers again, so client traffic is blocked rather than sent some other way.
/// </summary>
public sealed class ConnectionController(
    FacilityCatalog catalog,
    Connectors connectors,
    Relay relay,
    GatewaySettings settings,
    RecentLog log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(20);
    private const int MissesBeforeInterrupted = 2;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _sync = new();
    private CancellationTokenSource _attempt = new();
    private int _request;

    // What the connector last brought up, and so what has to be taken down. Only touched while _busy is held.
    private Facility? _up;

    private ConnectionState _state = ConnectionState.Disconnected;
    private Facility? _facility;
    private string? _client;
    private string? _message;

    public GatewayStatus Status
    {
        get
        {
            lock (_sync) return new GatewayStatus(_state, _facility?.Id, _message, _client, settings.RelayPort, log.Snapshot());
        }
    }

    /// <returns>False when there is no such facility.</returns>
    public bool Connect(string id, string client)
    {
        var facility = catalog.Find(id);
        if (facility is null) return false;
        lock (_sync)
        {
            var current = _facility?.Id == facility.Id
                && _state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;
            if (!current) Request(facility, client, ConnectionState.Connecting, $"Connecting to {facility.Name}");
        }
        return true;
    }

    public void Disconnect(string client)
    {
        lock (_sync)
        {
            if (_state != ConnectionState.Disconnected) Request(null, client, ConnectionState.Disconnecting, "Disconnecting");
        }
    }

    // Under _sync.
    private void Request(Facility? target, string client, ConnectionState state, string message)
    {
        _attempt.Cancel();
        _attempt = new CancellationTokenSource();
        var request = ++_request;
        var ct = _attempt.Token;
        (_state, _facility, _client, _message) = (state, target ?? _facility, client, message);
        log.Add($"{message} (asked by {client})");
        _ = Task.Run(() => CarryOutAsync(request, target, ct));
    }

    private async Task CarryOutAsync(int request, Facility? target, CancellationToken ct)
    {
        await _busy.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (request != _request) return; // A newer request takes it from here.
            }

            if (!await TearDownAsync(request))
                return;

            if (target is null)
                Set(request, ConnectionState.Disconnected, "Disconnected");
            else
                await ConnectAsync(request, target, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded; the newer request tears down whatever this one got as far as.
        }
        catch (Exception ex)
        {
            Set(request, ConnectionState.Failed, ex.Message);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task ConnectAsync(int request, Facility facility, CancellationToken ct)
    {
        _up = facility;
        var code = await connectors.RunAsync(facility, "Connect", ct);
        if (code != 0)
        {
            await FailAsync(request, $"The {facility.Connector} connector failed (exit code {code})");
            return;
        }

        if (facility.Check is { } check)
        {
            Set(request, ConnectionState.Connecting, $"Waiting for {check} to answer. Sign in on the gateway if the VPN client asks.");
            var deadline = DateTime.UtcNow.AddSeconds(facility.Timeout);
            while (!await Probe.TcpAsync(check, ct))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await FailAsync(request, $"{check} did not answer within {facility.Timeout} s");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        relay.Open(facility);
        Set(request, ConnectionState.Connected, $"Connected to {facility.Name}");
    }

    private async Task FailAsync(int request, string message)
    {
        Set(request, ConnectionState.Failed, message);
        await TearDownAsync(request);
    }

    /// <summary>Closes the relay and disconnects what is up. False, and Failed, if it still answers.</summary>
    private async Task<bool> TearDownAsync(int request)
    {
        relay.Close();
        if (_up is not { } facility) return true;

        await connectors.RunAsync(facility, "Disconnect", CancellationToken.None);
        if (facility.Check is { } check)
        {
            var deadline = DateTime.UtcNow + CleanupWait;
            while (await Probe.TcpAsync(check, CancellationToken.None))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Set(request, ConnectionState.Failed,
                        $"{facility.Name} still answers at {check}. Disconnect its VPN client on the gateway, then try again.");
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        _up = null;
        return true;
    }

    private void Set(int request, ConnectionState state, string message)
    {
        lock (_sync)
        {
            if (request != _request) return;
            (_state, _message) = (state, message);
            if (state == ConnectionState.Disconnected) _facility = null;
        }
        log.Add(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        var misses = 0;
        while (await timer.WaitForNextTickAsync(stop))
        {
            // Skip a beat while a request is being carried out.
            if (!await _busy.WaitAsync(0, stop)) continue;
            try
            {
                Facility? facility;
                ConnectionState state;
                int request;
                lock (_sync) (facility, state, request) = (_facility, _state, _request);

                if (facility?.Check is not { } check || state is not (ConnectionState.Connected or ConnectionState.Interrupted))
                {
                    misses = 0;
                    continue;
                }

                if (await Probe.TcpAsync(check, stop))
                {
                    misses = 0;
                    if (state == ConnectionState.Interrupted)
                    {
                        relay.Open(facility);
                        Set(request, ConnectionState.Connected, $"{facility.Name} answers again");
                    }
                }
                else if (state == ConnectionState.Connected && ++misses >= MissesBeforeInterrupted)
                {
                    relay.Close();
                    Set(request, ConnectionState.Interrupted, $"{check} stopped answering. Traffic is blocked until it answers again.");
                }
            }
            finally
            {
                _busy.Release();
            }
        }
    }

    /// <summary>A stopped gateway carries nothing: the relay closes and the VPN is disconnected.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        int request;
        lock (_sync)
        {
            _attempt.Cancel();
            request = _request;
        }
        await base.StopAsync(ct);
        await _busy.WaitAsync(ct);
        await TearDownAsync(request);
    }
}
