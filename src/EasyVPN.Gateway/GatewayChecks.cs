namespace EasyVPN.Gateway;

/// <summary>
/// What the gateway needs to do its job: the firewall letting workstations in, Internet Connection
/// Sharing to share a VPN with them, each facility's VPN client and settings (asked of its
/// connector), and for Layer 2 facilities a bridged "EasyVPN L2" adapter. Nothing here connects
/// anything.
///
/// Checked here, in the background, at the gateway's own pace - a connector's check is a PowerShell
/// script, run once for all its facilities alike (a hundred Tosibox Locks are one Tosi Client) - and
/// kept: a facility is checked again only when it, its connector or what its connector's facilities
/// share changes, or after a day; the gateway's own checks every few minutes. The EasyVPN Gateway app only reads what is
/// known (<see cref="Results"/>), which takes no time, and asks for a fresh round with Refresh. A
/// round once started is finished, whoever stops waiting for it.
/// </summary>
public sealed class GatewayChecks(FacilityCatalog catalog, Connectors connectors, Sharing sharing, GatewaySettings settings, RecentLog log) : IDisposable
{
    /// <summary>The rule the installer makes.</summary>
    private const string FirewallRule = "EasyVPN Gateway";

    private static readonly TimeSpan FacilityAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan SystemAge = TimeSpan.FromMinutes(10);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, (string Key, CheckResult Result, DateTime At)> _facilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<CheckResult> _system = [];
    private DateTime _systemAt = DateTime.MinValue;
    private DateTime _fresh = DateTime.MinValue;
    private Task? _round;

    /// <summary>
    /// What is known now, at once, and whether a round is under way. One starts, in the background,
    /// when something is missing or old - or, with <paramref name="refresh"/>, to check everything again.
    /// </summary>
    public GatewayCheckRun Results(bool refresh)
    {
        lock (_sync)
        {
            if (refresh) _fresh = DateTime.UtcNow;
            var facilities = catalog.All();
            if (_round is not { IsCompleted: false } && Due(facilities)) _round = Task.Run(RoundAsync);
            return new GatewayCheckRun(
                [.. _system, .. facilities.Select(f => _facilities.GetValueOrDefault(f.Id).Result).OfType<CheckResult>()],
                _round is { IsCompleted: false });
        }
    }

    public void Dispose() => _stop.Cancel();

    // Under _sync.
    private bool Due(IReadOnlyList<Facility> facilities) =>
        SystemDue() || facilities.Any(f => FacilityDue(f, connectors.Fingerprint(f)));

    private bool SystemDue() => _systemAt < DateTime.UtcNow - SystemAge || _systemAt < _fresh;

    private bool FacilityDue(Facility facility, string key) =>
        !_facilities.TryGetValue(facility.Id, out var known) || known.Key != key || known.At < DateTime.UtcNow - FacilityAge || known.At < _fresh;

    /// <summary>
    /// The checks due, in turn. The scripts run one at a time on a small machine - two processors -
    /// and on a bigger one half as many at once as it has processors: the gateway has VPN clients to
    /// drive, and they come first.
    /// </summary>
    private async Task RoundAsync()
    {
        var ct = _stop.Token;
        try
        {
            var facilities = catalog.All();
            bool system;
            lock (_sync) system = SystemDue();
            if (system)
            {
                var results = new List<CheckResult> { Firewall() };
                if (facilities.Any(f => !f.Layer2)) results.Add(await SharingAsync(ct));
                if (facilities.Any(f => f.Layer2)) results.Add(await Layer2Async(ct));
                lock (_sync) (_system, _systemAt) = (results, DateTime.UtcNow);
            }

            // Facilities alike in what their connector's check reads share one run: a hundred Tosibox
            // Locks are one Tosi Client, checked once.
            List<(Facility Facility, string Key)> due;
            lock (_sync) due = [.. facilities.Select(f => (f, connectors.Fingerprint(f))).Where(d => FacilityDue(d.f, d.Item2))];
            using var few = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
            await Task.WhenAll(due.GroupBy(d => connectors.CheckGroup(d.Facility)).Select(async group =>
            {
                await few.WaitAsync(ct);
                try
                {
                    var result = await connectors.CheckAsync(group.First().Facility, ct);
                    var at = DateTime.UtcNow;
                    lock (_sync)
                    {
                        foreach (var (facility, key) in group)
                            _facilities[facility.Id] = (key, connectors.ForFacility(result, facility), at);
                    }
                }
                finally
                {
                    few.Release();
                }
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The gateway is stopping.
        }
        catch (Exception ex)
        {
            log.Add($"Checking the facilities stopped: {ex.Message}");
        }
    }

    private CheckResult Firewall()
    {
        const string subject = "Firewall";
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            dynamic rule;
            try
            {
                rule = policy.Rules.Item(FirewallRule);
            }
            catch (Exception)
            {
                return new(subject, CheckLevel.Problem,
                    $"There is no \"{FirewallRule}\" rule, so workstations may not get in. Repair EasyVPN Gateway in Apps and Features.");
            }

            var ports = ((string)rule.LocalPorts).Split(',', StringSplitOptions.TrimEntries);
            if (!(bool)rule.Enabled)
                return new(subject, CheckLevel.Problem, $"The \"{FirewallRule}\" rule is turned off, so workstations cannot get in.");
            if (!ports.Contains($"{settings.ApiPort}"))
                return new(subject, CheckLevel.Warning,
                    $"The \"{FirewallRule}\" rule lets in TCP {rule.LocalPorts}, but this gateway uses {settings.ApiPort} (gateway.json).");
            return new(subject, CheckLevel.Ok, $"TCP {settings.ApiPort} is open to workstations.");
        }
        catch (Exception ex)
        {
            return new(subject, CheckLevel.Warning, $"Could not read the firewall's rules: {ex.Message}");
        }
    }

    private async Task<CheckResult> SharingAsync(CancellationToken ct)
    {
        const string subject = "Sharing";
        var (ok, shared) = await sharing.StatusAsync(ct);
        if (!ok)
            return new(subject, CheckLevel.Problem,
                $"Workstations reach a facility through Windows' Internet Connection Sharing, which cannot be used here: {shared}");
        return new(subject, CheckLevel.Ok, shared.Length == 0
            ? "Internet Connection Sharing is ready to share a facility's VPN with the workstations."
            : $"{shared} is shared with the workstations.");
    }

    private static async Task<CheckResult> Layer2Async(CancellationToken ct)
    {
        const string subject = "Layer 2";
        var adapter = $"\"{Defaults.Layer2Adapter}\"";
        return await Layer2Setup.StatusAsync(ct) switch
        {
            { Adapter: false, Tapctl: false } => new(subject, CheckLevel.Problem,
                "Layer 2 facilities need OpenVPN 2.6 or later on this gateway, installed with its TAP-Windows6 driver.",
                GatewayFix.OpenVpnDownload),
            { Adapter: false } => new(subject, CheckLevel.Problem,
                $"The {adapter} adapter is not there yet.", GatewayFix.Layer2Adapter),
            { Bridged: false } => new(subject, CheckLevel.Problem,
                $"{adapter} is not in a Network Bridge yet. In Network Connections, select it and the Layer 2 adapter of the VPN client (TOSIBOX Key's, say), right-click, and choose Bridge Connections.",
                GatewayFix.Bridge),
            _ => new(subject, CheckLevel.Ok, $"{adapter} is bridged with the VPN's Layer 2 adapter."),
        };
    }
}
