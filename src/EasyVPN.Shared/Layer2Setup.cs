namespace EasyVPN;

/// <summary>
/// Sets up the "EasyVPN L2" TAP adapter with layer2.ps1, shipped beside the exe. Making one takes
/// OpenVPN's tapctl and SYSTEM or an administrator, so the services do it, when it is needed.
/// </summary>
public static class Layer2Setup
{
    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "layer2.ps1");
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    /// <summary>Whether the adapter is there, whether it could be made, and whether it is in a Network Bridge.</summary>
    public sealed record State(bool Adapter, bool Tapctl, bool Bridged);

    public static async Task<State> StatusAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        await PowerShell.RunFileAsync(Script, ["Status"], new Dictionary<string, string>(), Limit, lines.Add, ct);
        bool Is(string name) => lines.Contains($"{name}=True", StringComparer.OrdinalIgnoreCase);
        return new State(Is("adapter"), Is("tapctl"), Is("bridged"));
    }

    /// <summary>Adds the adapter if it is missing; a workstation's also gets a metric that keeps its default route.</summary>
    /// <exception cref="InvalidOperationException">It could not be made, saying why (OpenVPN not installed, most likely).</exception>
    public static async Task EnsureAdapterAsync(bool workstation, Action<string> log, CancellationToken ct)
    {
        if (TapAdapter.Find(Defaults.Layer2Adapter) is not null) return;
        var lines = new List<string>();
        string[] args = workstation ? ["Create", "-Workstation"] : ["Create"];
        var code = await PowerShell.RunFileAsync(Script, args, new Dictionary<string, string>(), Limit, lines.Add, ct);
        foreach (var line in lines) log(line);
        if (code != 0) throw new InvalidOperationException(lines.LastOrDefault() ?? $"layer2.ps1 failed (exit code {code}).");
    }
}
