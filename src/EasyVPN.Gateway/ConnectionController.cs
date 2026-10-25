namespace EasyVPN.Gateway;

/// <summary>
/// The one facility connection the gateway carries, shared by every client.
///
/// Connecting first takes down whatever was up, then runs the facility's connector, waits until
/// the facility answers from here, and only then opens the way in to it - its VPN shared with the
/// workstations' tunnel (<see cref="Sharing"/>), or for a Layer 2 facility the Layer 2 port:
/// Connected means the facility was reached, not that a VPN client turned green. Each request
/// supersedes the one before - an attempt still in progress is cancelled, and only the newest
/// request is carried out.
///
/// Taking a facility down is only done once its check has stopped answering. Two facilities often
/// use the same addresses, and a VPN left up underneath would quietly take the next one's traffic.
///
/// While connected the facility is checked every few seconds. When it stops answering, the way in
/// closes until it answers again, so client traffic is blocked rather than sent some other way.
///
/// A routed facility without routes of its own gets the networks its VPN sets up while connecting
/// (<see cref="VpnRoutes"/>): clients route those, and the adapter they lead to is the one shared.
/// </summary>
public sealed class ConnectionController(
    FacilityCatalog catalog,
    Connectors connectors,
    Hub hub,
    Sharing sharing,
    Layer2Port layer2,
    GatewaySettings settings,
    RecentLog log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RoutesPoll = TimeSpan.FromSeconds(2);
    private const int MissesBeforeInterrupted = 2;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _sync = new();
    private CancellationTokenSource _attempt = new();
    private int _request;

    // What the connector last brought up, and so what has to be taken down. Only touched while _busy is held.
    private Facility? _up;

    // The hub's launch its adapter was last shared at, or null when it is not, or -1 when sharing
    // failed: a later launch made the adapter again, shared with nothing. Only touched while _busy is held.
    private int? _sharedAt;

    // Why the hub would not start, said once. Only touched while _busy is held.
    private string? _hubProblem;

    private ConnectionState _state = ConnectionState.Disconnected;
    private Facility? _facility;
    private string? _client;
    private string? _message;

    // The networks the way in opens to: the facility's routes, or those its VPN set up.
    private IReadOnlyList<string>? _routes;

    public GatewayStatus Status
    {
        get
        {
            lock (_sync) return new GatewayStatus(_state, _facility?.Id, _message, _client, settings.RelayPort, log.Snapshot(), _routes);
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
        (_state, _facility, _client, _message, _routes) = (state, target ?? _facility, client, message, null);
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
        if (facility.Layer2 && TapAdapter.Find(Defaults.Layer2Adapter) is null)
        {
            Set(request, ConnectionState.Failed,
                $"{facility.Name} is set to Layer 2, which needs an \"{Defaults.Layer2Adapter}\" adapter on the gateway, bridged with its VPN client's Layer 2 adapter, and this gateway has none. Add it in the EasyVPN Gateway app on the gateway, or turn Layer 2 off for {facility.Name} in the facility editor.");
            return;
        }

        var learn = !facility.Layer2 && facility.Routes.Count == 0;
        var before = learn ? VpnRoutes.Snapshot() : [];

        _up = facility;
        var code = await connectors.RunAsync(facility, "Connect", ct, line => Progress(request, line));
        if (code != 0)
        {
            await FailAsync(request, $"The {facility.Connector} connector failed (exit code {code})");
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(facility.Timeout);
        if (facility.Check is { } check)
        {
            Set(request, ConnectionState.Connecting, $"Waiting for {check} to answer. Sign in on the gateway if the VPN client asks.");
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

        var routes = facility.Routes;
        if (learn)
        {
            Set(request, ConnectionState.Connecting, "Waiting for the networks its VPN sets up. Sign in on the gateway if the VPN client asks.");
            routes = await LearnRoutesAsync(before, deadline, ct);
            if (routes.Count == 0)
            {
                await FailAsync(request,
                    $"{facility.Name}'s VPN set up no networks on this gateway within {facility.Timeout} s. Give the facility its routes in the facility editor.");
                return;
            }
            log.Add($"{facility.Name}'s VPN set up {string.Join(", ", routes)}");
        }

        lock (_sync)
        {
            if (request == _request) _routes = facility.Layer2 ? null : routes;
        }
        if (!facility.Layer2) Set(request, ConnectionState.Connecting, $"Sharing {facility.Name}'s VPN with the workstations");
        try
        {
            await OpenWayInAsync(facility, ct);
        }
        catch (InvalidOperationException ex)
        {
            await FailAsync(request, ex.Message);
            return;
        }
        Set(request, ConnectionState.Connected, $"Connected to {facility.Name}");
    }

    /// <summary>
    /// The networks the VPN has added since <paramref name="before"/>, once they hold still: a VPN
    /// client adds its routes a few seconds after it connects, and not always all at once.
    /// </summary>
    private static async Task<List<string>> LearnRoutesAsync(IReadOnlyList<VpnRoutes.Route> before, DateTime deadline, CancellationToken ct)
    {
        List<string> last = [];
        while (true)
        {
            var now = VpnRoutes.Added(before, VpnRoutes.Snapshot());
            if (now.Count > 0 && now.SequenceEqual(last)) return now;
            if (DateTime.UtcNow >= deadline) return now;
            last = now;
            await Task.Delay(RoutesPoll, ct);
        }
    }

    private async Task FailAsync(int request, string message)
    {
        Set(request, ConnectionState.Failed, message);
        await TearDownAsync(request);
    }

    /// <summary>
    /// How clients reach a connected facility: for a routed one, the adapter its networks lead to -
    /// its VPN's - shared with the tunnel; for a bridged one, the Layer 2 port.
    /// </summary>
    /// <exception cref="InvalidOperationException">The VPN could not be shared.</exception>
    private async Task OpenWayInAsync(Facility facility, CancellationToken ct)
    {
        if (facility.Layer2)
        {
            layer2.Open();
            return;
        }
        IReadOnlyList<string> routes;
        lock (_sync) routes = _routes ?? facility.Routes;
        var vpn = Sharing.Carrying(routes)
            ?? throw new InvalidOperationException($"This gateway has no route to {string.Join(", ", routes)} to share with the workstations.");
        var launch = hub.Launches;
        // Marked first, so a share that fails halfway is undone all the same, and tried again.
        _sharedAt = -1;
        await sharing.ShareAsync(vpn, ct);
        _sharedAt = launch;
    }

    private async Task CloseWayInAsync()
    {
        layer2.Close();
        if (_sharedAt is null) return;
        _sharedAt = null;
        await sharing.UnshareAsync();
    }

    /// <summary>Closes the way in and disconnects what is up. False, and Failed, if it still answers.</summary>
    private async Task<bool> TearDownAsync(int request)
    {
        await CloseWayInAsync();
        if (_up is not { } facility) return true;

        await connectors.RunAsync(facility, "Disconnect", CancellationToken.None, line => Progress(request, line));
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

    /// <summary>
    /// What a connector says while it works, as the state's message - so whoever asked sees it
    /// getting on - without logging it again. A line that only introduces the next ("On …'s
    /// desktop:") is left out.
    /// </summary>
    private void Progress(int request, string line)
    {
        if (line.EndsWith(':')) return;
        lock (_sync)
        {
            if (request == _request && _state is ConnectionState.Connecting or ConnectionState.Disconnecting) _message = line;
        }
    }

    private void Set(int request, ConnectionState state, string message)
    {
        lock (_sync)
        {
            if (request != _request) return;
            (_state, _message) = (state, message);
            if (state is ConnectionState.Disconnected or ConnectionState.Failed) _routes = null;
            if (state == ConnectionState.Disconnected) _facility = null;
        }
        log.Add(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // The tunnel runs as long as the gateway does. Sharing left over from before - a crash, a
        // restart of Windows - carries nothing any more.
        await _busy.WaitAsync(stop);
        try
        {
            StartHub();
            await sharing.UnshareAsync();
        }
        finally
        {
            _busy.Release();
        }

        using var timer = new PeriodicTimer(CheckInterval);
        var misses = 0;
        while (await timer.WaitForNextTickAsync(stop))
        {
            // Skip a beat while a request is being carried out.
            if (!await _busy.WaitAsync(0, stop)) continue;
            try
            {
                if (!hub.IsRunning) StartHub();

                Facility? facility;
                ConnectionState state;
                int request;
                lock (_sync) (facility, state, request) = (_facility, _state, _request);

                // gost started again, and made its adapter again: shared with nothing.
                if (facility is not null && state == ConnectionState.Connected && _sharedAt is { } shared && shared != hub.Launches)
                {
                    try
                    {
                        await OpenWayInAsync(facility, stop);
                    }
                    catch (InvalidOperationException ex)
                    {
                        log.Add(ex.Message);
                    }
                }

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
                        try
                        {
                            await OpenWayInAsync(facility, stop);
                            Set(request, ConnectionState.Connected, $"{facility.Name} answers again");
                        }
                        catch (InvalidOperationException ex)
                        {
                            log.Add(ex.Message);
                        }
                    }
                }
                else if (state == ConnectionState.Connected && ++misses >= MissesBeforeInterrupted)
                {
                    await CloseWayInAsync();
                    Set(request, ConnectionState.Interrupted, $"{check} stopped answering. Traffic is blocked until it answers again.");
                }
            }
            finally
            {
                _busy.Release();
            }
        }
    }

    /// <summary>
    /// Starts the tunnel's end here. One that cannot start - gost.exe gone, say, taken by an
    /// antivirus - is said once, and tried again every few seconds; the gateway runs on meanwhile.
    /// </summary>
    private void StartHub()
    {
        try
        {
            hub.Start();
            _hubProblem = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            var problem = $"The tunnel for the workstations could not be started: {ex.Message}";
            if (problem != _hubProblem) log.Add(problem);
            _hubProblem = problem;
        }
    }

    /// <summary>A stopped gateway carries nothing: the VPN is no longer shared, and is disconnected.</summary>
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
        hub.Stop();
    }
}
