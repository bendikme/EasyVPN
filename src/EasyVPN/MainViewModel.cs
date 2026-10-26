using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>
/// The window and the tray menu: the gateway's facilities, in their groups, and the one
/// connection there is. All of it comes from the agent, asked every second.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan FacilityRefresh = TimeSpan.FromMinutes(1);

    private readonly AgentClient _agent;
    private readonly UserSettings _user;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _search = "";
    private bool _favouritesOnly;
    private FacilityViewModel? _selected;
    private AgentStatus? _status;
    private AgentSettingsView? _settings;
    private bool _agentOnline = true;
    private string? _error;
    private DateTime _loaded = DateTime.MinValue;
    private bool _polling;
    private bool _updating;
    private DateTime _askedAt = DateTime.MinValue;
    private bool _moreActivity;
    private ListRow? _selectedRow;
    private string? _activeId;

    // Rows are kept from one build of the list to the next, so the list keeps its place.
    private readonly Dictionary<string, GroupRow> _groupRows = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, FacilityRow> _facilityRows = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(AgentClient agent, UserSettings user)
    {
        _agent = agent;
        _user = user;

        ConnectCommand = new AsyncRelayCommand(p => ConnectAsync(p as FacilityViewModel ?? Selected));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        ToggleFavouriteCommand = new RelayCommand(p => ToggleFavourite(p as FacilityViewModel ?? Selected));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenLinkCommand = new RelayCommand(p => Try(() => Launcher.Open(((FacilityLink)p!).Url)));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleGroupCommand = new RelayCommand(p =>
        {
            if (p is not GroupRow group) return;
            _user.SetOpen(group.Path, !group.IsOpen);
            Rebuild();
        });
        MoreActivityCommand = new RelayCommand(() =>
        {
            _moreActivity = !_moreActivity;
            Raise(nameof(Log), nameof(MoreActivityText));
        });
        UpdateCommand = new AsyncRelayCommand(UpdateAsync);

        _timer.Tick += async (_, _) => await PollAsync();
    }

    public ObservableCollection<FacilityViewModel> Facilities { get; } = [];

    /// <summary>
    /// The list as shown: the groups, and the facilities in those open - or, while searching (or
    /// showing favourites), every group with one to show in it, open.
    /// </summary>
    public ObservableCollection<ListRow> Rows { get; } = [];

    /// <summary>This user's favourites, recent facilities and open groups, which the tray menu shows too.</summary>
    public UserSettings User => _user;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand ToggleFavouriteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Opens or closes a group.</summary>
    public RelayCommand ToggleGroupCommand { get; }

    /// <summary>The activity's last few lines, or more of them.</summary>
    public RelayCommand MoreActivityCommand { get; }

    /// <summary>Installs the newer version found, or else looks for one.</summary>
    public AsyncRelayCommand UpdateCommand { get; }

    /// <summary>The agent is about to install an update, which replaces this app too.</summary>
    public event Action? UpdateInstalling;

    /// <summary>After each look at the agent: the connection may have changed.</summary>
    public event Action? Polled;

    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Rebuild();
        }
    }

    public bool FavouritesOnly
    {
        get => _favouritesOnly;
        set
        {
            if (!SetProperty(ref _favouritesOnly, value)) return;
            OnPropertyChanged(nameof(ShowAll));
            Rebuild();
        }
    }

    /// <summary>The other filter chip.</summary>
    public bool ShowAll
    {
        get => !_favouritesOnly;
        set
        {
            if (value) FavouritesOnly = false;
        }
    }

    public bool HasNoFacilities => Configured && Facilities.Count == 0;

    public bool NoMatches => Facilities.Count > 0 && Rows.Count == 0;

    public FacilityViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            _selectedRow = value is null ? null : _facilityRows.GetValueOrDefault(value.Id) is { } row && Rows.Contains(row) ? row : null;
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>The list's selection. Only a facility is selected: a group opens or closes instead.</summary>
    public ListRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (value is FacilityRow row) Selected = row.Facility;
            else OnPropertyChanged();
        }
    }

    /// <summary>Shows a facility in the list, its groups opened, and selects it.</summary>
    public void Show(FacilityViewModel facility)
    {
        var path = "";
        foreach (var name in facility.GroupNames)
        {
            path = path.Length == 0 ? name : $"{path}/{name}";
            if (!_user.IsOpen(path, OpenByDefault)) _user.SetOpen(path, true);
        }
        if (_favouritesOnly && !facility.IsFavourite) FavouritesOnly = false;
        Rebuild();
        Selected = facility;
    }

    private bool OpenByDefault => Facilities.Count <= FacilityTree.OpenUpTo;

    /// <summary>The list again, after something outside it - the tray menu - opened or closed a group.</summary>
    public void RebuildList() => Rebuild();

    /// <summary>The rows again, changed in place: rows kept from the last build keep their place in the list.</summary>
    private void Rebuild()
    {
        var query = _search.Trim();
        var rows = new List<ListRow>();
        FacilityTree.Append(
            rows,
            Facilities.Where(f => (!_favouritesOnly || f.IsFavourite) && f.Matches(query)),
            searching: query.Length > 0 || _favouritesOnly,
            path => _user.IsOpen(path, OpenByDefault),
            path => _groupRows.TryGetValue(path, out var row) ? row : _groupRows[path] = new GroupRow(path),
            facility => _facilityRows.TryGetValue(facility.Id, out var row) && row.Facility == facility ? row : _facilityRows[facility.Id] = new FacilityRow(facility),
            int.MaxValue,
            out _);

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count)
            {
                if (!ReferenceEquals(Rows[i], rows[i])) Rows[i] = rows[i];
            }
            else Rows.Add(rows[i]);
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);

        _selectedRow = _selected is not null && _facilityRows.GetValueOrDefault(_selected.Id) is { } selected && Rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(SelectedRow));
    }

    // ---------------------------------------------------------------- the connection

    public bool AgentOnline => _agentOnline;
    public bool Configured => _status?.Configured ?? false;
    public bool ShowSetup => _agentOnline && !Configured;
    public ConnectionState State => _status?.State ?? ConnectionState.Disconnected;
    public FacilityViewModel? Active => Find(_status?.FacilityId);

    /// <summary>There is a connection to end.</summary>
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;

    /// <summary>What the connection is doing, as this PC's agent says: the gateway's latest step, or how it ended.</summary>
    public string? StatusMessage => _status?.Message;

    /// <summary>The last thing that went wrong in the app itself (the agent's own troubles are in its message).</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>What this PC and the gateway did lately, newest first: the last few lines, or with <see cref="MoreActivityCommand"/> more.</summary>
    public IReadOnlyList<ActivityLine> Log => [.. (_status?.Log ?? []).Reverse().Take(_moreActivity ? 30 : 3).Select(ActivityLine.Of)];

    public bool HasActivity => _status?.Log is { Count: > 0 };

    public string MoreActivityText => _moreActivity ? "Show less" : "Show more";

    // ---------------------------------------------------------------- the selected facility

    public bool HasSelection => _selected is not null;
    public bool SelectedIsActive => _selected is not null && _selected == Active;
    public ConnectionState SelectedState => SelectedIsActive ? State : ConnectionState.Disconnected;
    public bool CanConnect => _selected is not null && !(SelectedIsActive && IsBusy);
    public bool CanDisconnect => SelectedIsActive && IsBusy;

    public string SelectedStateText => SelectedState switch
    {
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Connected => "Connected",
        ConnectionState.Interrupted => "Interrupted",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => "Failed",
        _ => "Not connected",
    };

    public string FavouriteText => _selected?.IsFavourite == true ? "Remove favourite" : "Add to favourites";

    public string? SelectedMessage =>
        SelectedIsActive ? _status?.Message
        : IsBusy && Active is { } active ? $"Connecting here ends the connection to {active.Name}."
        : null;

    // ---------------------------------------------------------------- footer and tray

    public string GatewayText =>
        !_agentOnline ? "The EasyVPN Agent service is not running on this PC"
        : !Configured ? "No gateway chosen"
        : $"Gateway {_settings?.Gateway} · {(_status!.GatewayOnline ? "online" : "not answering")}";

    public bool GatewayOnline => _status?.GatewayOnline ?? false;

    public string TunnelText => _status?.TunnelUp == true ? "Tunnel up" : "Tunnel down";

    // ---------------------------------------------------------------- updates

    public UpdateStatus? Update => _status?.Update;

    public bool CanInstallUpdate => Update is { State: UpdateState.Available or UpdateState.Failed, Latest: not null } update
        && Version.TryParse(update.Latest, out var latest) && Version.TryParse(update.Current, out var current) && latest > current;

    public string UpdateText => Update switch
    {
        null => "",
        { State: UpdateState.Checking } => "Looking for updates…",
        { State: UpdateState.Available } u => $"EasyVPN {u.Latest} is available",
        { State: UpdateState.Downloading } u => $"Downloading EasyVPN {u.Latest}…",
        { State: UpdateState.Installing } u => $"Installing EasyVPN {u.Latest}…",
        { State: UpdateState.Failed } u => u.Message ?? "The update failed",
        { State: UpdateState.UpToDate } u => $"Version {u.Current}, up to date",
        var u => u.Message ?? $"Version {u.Current}",
    };

    public string UpdateButtonText => CanInstallUpdate ? $"Install {Update!.Latest}" : "Check for updates";

    public bool CanUpdate => _agentOnline && Update is { State: not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) };

    public string TrayText => State switch
    {
        _ when !_agentOnline => "EasyVPN: the agent service is not running",
        ConnectionState.Connected => $"EasyVPN: connected to {Active?.Name}",
        ConnectionState.Connecting => $"EasyVPN: connecting to {Active?.Name}",
        ConnectionState.Interrupted => $"EasyVPN: {Active?.Name} interrupted",
        ConnectionState.Disconnecting => $"EasyVPN: disconnecting {Active?.Name}",
        ConnectionState.Failed => $"EasyVPN: could not connect to {Active?.Name}",
        _ when Configured && !GatewayOnline => "EasyVPN: the gateway is not answering",
        _ => "EasyVPN",
    };

    /// <summary>The tray icon's colour: grey when nothing can connect, amber while connecting, green connected, red when it went wrong.</summary>
    public TrayLook TrayLook => State switch
    {
        _ when !_agentOnline => TrayLook.Off,
        // Not asked yet.
        _ when _status is null => TrayLook.Idle,
        ConnectionState.Connecting or ConnectionState.Disconnecting => TrayLook.Busy,
        ConnectionState.Connected => TrayLook.Connected,
        ConnectionState.Interrupted or ConnectionState.Failed => TrayLook.Problem,
        _ when !Configured || !GatewayOnline => TrayLook.Off,
        _ => TrayLook.Idle,
    };

    /// <summary>The connection as the last poll saw it, for the notifications.</summary>
    public ConnectionView Connection => new(_agentOnline, State, _status?.FacilityId, Active?.Name, StatusMessage);

    /// <summary>This app asked to connect or disconnect within the last minute: what follows is no news to this user.</summary>
    public bool AskedRecently => DateTime.UtcNow - _askedAt < TimeSpan.FromMinutes(1);

    // ---------------------------------------------------------------- work

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    /// <summary>After the gateway settings changed: start over with what the agent now has.</summary>
    public async Task ReloadAsync()
    {
        Facilities.Clear();
        await PollAsync();
        await LoadAsync();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            try
            {
                _status = await _agent.StatusAsync();
                _agentOnline = true;
            }
            catch (AgentException)
            {
                _status = null;
                _agentOnline = false;
            }

            if (_status?.Update?.State == UpdateState.Installing && !_updating)
            {
                _updating = true;
                UpdateInstalling?.Invoke();
            }

            var due = DateTime.UtcNow - _loaded > (Facilities.Count == 0 ? TimeSpan.FromSeconds(10) : FacilityRefresh);
            if (Configured && due) await LoadAsync();

            UpdateStates();
            OnPropertyChanged(string.Empty);
            Polled?.Invoke();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task LoadAsync()
    {
        _loaded = DateTime.UtcNow;
        try
        {
            _settings = await _agent.SettingsAsync();
            var facilities = await _agent.FacilitiesAsync();
            var selected = _selected?.Id;
            Facilities.Clear();
            foreach (var info in facilities) Facilities.Add(new FacilityViewModel(info, _user.IsFavourite(info.Id)));
            UpdateStates();
            Rebuild();
            Selected = Find(selected) ?? Active ?? Rows.OfType<FacilityRow>().FirstOrDefault()?.Facility ?? Facilities.FirstOrDefault();
            Error = null;
        }
        catch (AgentException ex)
        {
            Error = ex.Message;
        }
    }

    private async Task ConnectAsync(FacilityViewModel? facility)
    {
        if (facility is null) return;
        Selected = facility;
        _askedAt = DateTime.UtcNow;
        await Try(() => _agent.ConnectAsync(facility.Id));
        if (Error is null) _user.Used(facility.Id);
        await PollAsync();
    }

    private async Task DisconnectAsync()
    {
        _askedAt = DateTime.UtcNow;
        await Try(_agent.DisconnectAsync);
        await PollAsync();
    }

    private async Task UpdateAsync()
    {
        if (CanInstallUpdate) await Try(_agent.InstallUpdateAsync);
        else await Try(() => _agent.CheckUpdatesAsync());
        await PollAsync();
    }

    private void ToggleFavourite(FacilityViewModel? facility)
    {
        if (facility is null) return;
        facility.IsFavourite = !facility.IsFavourite;
        _user.SetFavourite(facility.Id, facility.IsFavourite);
        if (_favouritesOnly) Rebuild();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Marks the facility connected; the groups say which one holds it, so the list is built again when it changes.</summary>
    private void UpdateStates()
    {
        foreach (var facility in Facilities)
            facility.State = facility == Active && State != ConnectionState.Disconnected ? State : null;
        var active = Facilities.FirstOrDefault(f => f.IsActive)?.Id;
        if (active == _activeId) return;
        _activeId = active;
        Rebuild();
    }

    private FacilityViewModel? Find(string? id) =>
        id is null ? null : Facilities.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
            Error = null;
        }
        catch (AgentException ex)
        {
            Error = ex.Message;
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
            Error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>One line of the activity: when, where - the gateway, or this PC - and what.</summary>
public sealed record ActivityLine(DateTimeOffset Time, string Source, string Text)
{
    // The agent marks the gateway's lines so.
    private const string FromGateway = "Gateway: ";

    public static ActivityLine Of(LogLine line) => line.Text.StartsWith(FromGateway, StringComparison.Ordinal)
        ? new(line.Time, "Gateway", line.Text[FromGateway.Length..])
        : new(line.Time, "This PC", line.Text);
}
