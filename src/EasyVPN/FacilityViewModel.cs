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
    public string Routes => string.Join(", ", Info.Routes);
    public string Summary => $"{Info.Connector} · {Routes}";
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
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool IsActive => _state is not null;

    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Customer.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Routes.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Notes?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
