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
/// While connected the facility is watched every few seconds: its VPN must still carry its networks,
/// and its check, if it has one, answer. When either stops, the way in closes until it is back, so
/// client traffic is blocked rather than sent some other way.
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
    private static readonly TimeSpan RoutesPoll = TimeSpan.FromSeconds(1);
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

    // The adapter shared then, by its id. Only touched while _busy is held.
    private string? _sharedAdapter;

    // The VPN's adapter, by its id: where the facility's networks led when it was shared, kept while
    // it is interrupted. And while they lead somewhere else - its VPN gone - where that is. Only
    // touched while _busy is held.
    private string? _vpn;
    private string? _fallback;

    // The networks it carried then: what has to be gone once its VPN client has disconnected. Only
    // touched while _busy is held.
    private IReadOnlyList<string>? _vpnRoutes;

    private readonly LearnedRoutes _learned = new();

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

            if (!await TearDownAsync(request, switching: target is not null))
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
            // Sharing left on for a switch goes with it.
            await Settled(CloseWayInAsync());
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
        IReadOnlyList<string> remembered = learn ? _learned.Of(facility.Id) : [];

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
        // Shared as soon as it is plain which adapter the VPN's networks are on, while they are
        // still waited on to hold still: sharing takes some seconds of its own.
        Task? early = null;
        if (learn)
        {
            Set(request, ConnectionState.Connecting, "Waiting for the networks its VPN sets up. Sign in on the gateway if the VPN client asks.");
            try
            {
                routes = await LearnRoutesAsync(before, remembered, deadline, ct, found =>
                {
                    if (early is not null || Sharing.Carrying(found) is not { } adapter) return;
                    Set(request, ConnectionState.Connecting, $"Sharing {facility.Name}'s VPN with the workstations");
                    early = ShareAsync(adapter, ct);
                });
            }
            finally
            {
                // Never left running under what comes next. Should it have failed, sharing is tried
                // once more below, and says why.
                if (early is not null) await Settled(early);
            }
            if (routes.Count == 0)
            {
                await FailAsync(request,
                    $"{facility.Name}'s VPN set up no networks on this gateway within {facility.Timeout} s. Was it connected already, before EasyVPN connected it? Then disconnect it there and connect again; or give the facility its routes in the facility editor.");
                return;
            }
            log.Add($"{facility.Name}'s VPN set up {string.Join(", ", routes)}");
            if (!routes.SequenceEqual(remembered)) _learned.Remember(facility.Id, routes);
        }

        lock (_sync)
        {
            if (request == _request) _routes = facility.Layer2 ? null : routes;
        }
        if (!facility.Layer2 && early is null) Set(request, ConnectionState.Connecting, $"Sharing {facility.Name}'s VPN with the workstations");
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
    /// <param name="found">Told what there is so far, each time there is some.</param>
    /// <param name="remembered">What it set up the last time it connected (<see cref="LearnedRoutes"/>).</param>
    private static async Task<List<string>> LearnRoutesAsync(IReadOnlyList<VpnRoutes.Route> before, IReadOnlyList<string> remembered, DateTime deadline, CancellationToken ct, Action<List<string>>? found = null)
    {
        List<string> last = [];
        while (true)
        {
            var snapshot = VpnRoutes.Snapshot();
            var now = VpnRoutes.Added(before, snapshot);
            // What it set up last time, and nothing more: done, without waiting for it to hold still.
            if (remembered.Count > 0 && now.Count == remembered.Count && remembered.All(now.Contains)) return now;
            // Up before it was asked - a Lock connected in Tosi Client already - so nothing is new:
            // what it set up last time is there.
            if (now.Count == 0 && VpnRoutes.AllPresent(remembered, snapshot)) return [.. remembered];
            if (now.Count > 0) found?.Invoke(now);
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
            // Sharing left on from the facility before goes: this one is bridged.
            if (_sharedAt is not null)
            {
                (_sharedAt, _sharedAdapter) = (null, null);
                await sharing.UnshareAsync();
            }
            layer2.Open();
            return;
        }
        IReadOnlyList<string> routes;
        lock (_sync) routes = _routes ?? facility.Routes;
        var vpn = Sharing.Carrying(routes)
            ?? throw new InvalidOperationException($"This gateway has no route to {string.Join(", ", routes)} to share with the workstations.");
        _vpnRoutes = routes;
        await ShareAsync(vpn, ct);
    }

    /// <exception cref="InvalidOperationException">Windows would not share it.</exception>
    private async Task ShareAsync(System.Net.NetworkInformation.NetworkInterface vpn, CancellationToken ct)
    {
        var launch = hub.Launches;
        // Shared with it already: while its networks were coming in, or for the facility before, on
        // the same VPN client's adapter. Sharing takes seconds, so it is not done again.
        if (_sharedAt != launch || _sharedAdapter != vpn.Id)
        {
            // Marked first, so a share that fails halfway is undone all the same, and tried again.
            (_sharedAt, _sharedAdapter) = (-1, null);
            await sharing.ShareAsync(vpn, ct);
        }
        (_sharedAt, _sharedAdapter, _vpn, _fallback) = (launch, vpn.Id, vpn.Id, null);
    }

    /// <summary>Waits for a task, whatever came of it.</summary>
    private static async Task Settled(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // What came of it is looked at again by whoever needs it.
        }
    }

    private async Task CloseWayInAsync()
    {
        layer2.Close();
        if (_sharedAt is null) return;
        (_sharedAt, _sharedAdapter) = (null, null);
        await sharing.UnshareAsync();
    }

    /// <summary>Closes the way in and disconnects what is up. False, and Failed, if it still answers.</summary>
    /// <param name="switching">
    /// Another facility follows. Sharing is left on: the next one most often goes through the same
    /// VPN client's adapter - every Tosibox Lock through Tosi Client's - and turning sharing off and
    /// on again takes seconds. The workstations route nothing to the gateway meanwhile. It does go
    /// should this facility not go down, and when the next one ends up on another adapter.
    /// </param>
    private async Task<bool> TearDownAsync(int request, bool switching = false)
    {
        // Sharing goes while the VPN client is told to disconnect, not before: should Windows' sharing
        // hang, the facility still goes down meanwhile.
        Task closing;
        if (switching)
        {
            layer2.Close();
            closing = Task.CompletedTask;
        }
        else closing = CloseWayInAsync();
        if (_up is not { } facility)
        {
            await closing;
            (_vpn, _fallback, _vpnRoutes) = (null, null, null);
            return true;
        }

        var code = await connectors.RunAsync(facility, "Disconnect", CancellationToken.None, line => Progress(request, line));
        await closing;
        var deadline = DateTime.UtcNow + CleanupWait;
        if (facility.Check is { } check)
        {
            while (await Probe.TcpAsync(check, CancellationToken.None))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await CloseWayInAsync();
                    Set(request, ConnectionState.Failed,
                        $"{facility.Name} still answers at {check}. Disconnect its VPN client on the gateway, then try again.");
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        else if (_vpn is { } vpn && _vpnRoutes is { Count: > 0 } routes && vpn != Sharing.Carrying(["1.1.1.1/32"])?.Id)
        {
            // Without a check, its networks going from its VPN is what says it is down. A VPN client
            // that was not made to disconnect - on a desktop that took no clicks, say - still carries
            // them, and the next facility, often on the same addresses, must not go up on top of it.
            // (A facility the gateway reaches through its own network, not a VPN, has nothing to go.)
            while (Sharing.Carrying(routes) is { OperationalStatus: System.Net.NetworkInformation.OperationalStatus.Up } still && still.Id == vpn)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await CloseWayInAsync();
                    Set(request, ConnectionState.Failed,
                        $"{facility.Name} is still connected on the gateway: its VPN still carries {string.Join(", ", routes)}{(code != 0 ? $", and its {facility.Connector} connector failed (exit code {code})" : "")}. Disconnect it in its VPN client on the gateway, or disconnect again here.");
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        (_vpn, _fallback, _vpnRoutes) = (null, null, null);
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

                if (facility is null || state is not (ConnectionState.Connected or ConnectionState.Interrupted))
                {
                    misses = 0;
                    continue;
                }

                if (await ProblemAsync(facility, state, stop) is not { } problem)
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
                    Set(request, ConnectionState.Interrupted, problem);
                }
            }
            finally
            {
                _busy.Release();
            }
        }
    }

    /// <summary>
    /// Why a connected facility cannot be reached now, or null when it can. Its VPN must still carry
    /// its networks - they lead into the VPN's adapter, and it is up - which is watched for every
    /// routed facility, so one without a check is not taken to be connected after its VPN client
    /// lost the connection (a Tosibox Lock gone from Tosi Client takes its networks with it). And
    /// its check, when it has one, must answer.
    /// </summary>
    private async Task<string?> ProblemAsync(Facility facility, ConnectionState state, CancellationToken ct)
    {
        if (!facility.Layer2 && _vpn is { } vpn)
        {
            IReadOnlyList<string> routes;
            lock (_sync) routes = _routes ?? facility.Routes;
            var now = Sharing.Carrying(routes);
            var up = now?.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
            // Where the networks lead while the VPN is gone - the internet's adapter, most often - so
            // a VPN back on another adapter of its own is told apart from that.
            if (state == ConnectionState.Connected) _fallback = up && now!.Id == vpn ? null : now?.Id;
            var carried = up && (now!.Id == vpn || (state == ConnectionState.Interrupted && _fallback is not null && now.Id != _fallback));
            if (!carried)
                return $"{facility.Name}'s VPN no longer carries its networks: its VPN client lost the connection. Traffic is blocked until it is back.";
        }
        if (facility.Check is { } check && !await Probe.TcpAsync(check, ct))
            return $"{check} stopped answering. Traffic is blocked until it answers again.";
        return null;
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
