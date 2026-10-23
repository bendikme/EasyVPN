namespace EasyVPN.Gateway;

/// <summary>
/// What the gateway needs to do its job, checked when the EasyVPN Gateway app asks: the firewall
/// letting workstations in, each facility's VPN client and settings (asked of its connector), and
/// for Layer 2 facilities a bridged "EasyVPN L2" adapter. Nothing here connects anything.
/// </summary>
public sealed class GatewayChecks(FacilityCatalog catalog, Connectors connectors, GatewaySettings settings)
{
    /// <summary>The rule the installer makes.</summary>
    private const string FirewallRule = "EasyVPN Gateway";

    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        // One run at a time: each check is a PowerShell of its own.
        await _running.WaitAsync(ct);
        try
        {
            var facilities = catalog.All();
            var results = new List<CheckResult> { Firewall() };
            if (facilities.Any(f => f.Layer2)) results.Add(await Layer2Async(ct));

            using var few = new SemaphoreSlim(4);
            results.AddRange(await Task.WhenAll(facilities.Select(async facility =>
            {
                await few.WaitAsync(ct);
                try
                {
                    return await connectors.CheckAsync(facility, ct);
                }
                finally
                {
                    few.Release();
                }
            })));
            return results;
        }
        finally
        {
            _running.Release();
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
            if (!ports.Contains($"{settings.ApiPort}") || !ports.Contains($"{settings.RelayPort}"))
                return new(subject, CheckLevel.Warning,
                    $"The \"{FirewallRule}\" rule lets in TCP {rule.LocalPorts}, but this gateway uses {settings.ApiPort} and {settings.RelayPort} (gateway.json).");
            return new(subject, CheckLevel.Ok, $"TCP {settings.ApiPort} and {settings.RelayPort} are open to workstations.");
        }
        catch (Exception ex)
        {
            return new(subject, CheckLevel.Warning, $"Could not read the firewall's rules: {ex.Message}");
        }
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
