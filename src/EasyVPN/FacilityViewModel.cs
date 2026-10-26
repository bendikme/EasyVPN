namespace EasyVPN;

/// <summary>One facility in the list: what the gateway says about it, plus this user's star.</summary>
public sealed class FacilityViewModel(FacilityInfo info, bool favourite) : ObservableObject
{
    private bool _favourite = favourite;
    private ConnectionState? _state;

    public FacilityInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Customer => string.IsNullOrWhiteSpace(Info.Customer) ? "Other" : Info.Customer;

    /// <summary>The groups it is in, outermost first: its own group's, nested with '/', or else its customer.</summary>
    public IReadOnlyList<string> GroupNames { get; } =
        (info.Group ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } names
            ? names
            : [string.IsNullOrWhiteSpace(info.Customer) ? "Other" : info.Customer.Trim()];

    public string GroupPath => string.Join(" / ", GroupNames);
    public string Routes => Info.Routes.Count == 0 && !Info.Layer2 ? "those its VPN sets up when it connects" : string.Join(", ", Info.Routes);
    public string Summary => $"{Info.Connector} · {(Info.Layer2 ? "Layer 2" : Info.Routes.Count == 0 ? "networks from its VPN" : Routes)}";

    /// <summary>The adapter on this PC it comes in on, and how.</summary>
    public string Link => Info.Layer2
        ? $"{Defaults.Layer2Adapter}, on the facility's LAN (Layer 2)"
        : $"{Defaults.TunnelAdapter}, routed";
    public string? Dns => Info.Dns is { Domains.Count: > 0 } dns ? $"{string.Join(", ", dns.Domains)} via {string.Join(", ", dns.Servers)}" : null;
    public string? Check => Info.Check;
    public string? Notes => Info.Notes;
    public IReadOnlyList<FacilityLink> Links => Info.Links;

    public bool IsFavourite
    {
        get => _favourite;
        set => SetProperty(ref _favourite, value);
    }

    /// <summary>This facility's state while it is the one connected (or the last to fail); null otherwise.</summary>
    public ConnectionState? State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) Raise(nameof(IsActive), nameof(IsUp));
        }
    }

    public bool IsActive => _state is not null;

    /// <summary>Connecting, connected, or on its way down: nothing to connect. One that failed can be connected again.</summary>
    public bool IsUp => _state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted or ConnectionState.Disconnecting;

    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Customer.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || GroupPath.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Info.Connector.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Routes.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Notes?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
