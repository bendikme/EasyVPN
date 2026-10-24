using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>
/// The window and the tray menu: the gateway's facilities, grouped by customer, and the one
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

    public MainViewModel(AgentClient agent, UserSettings user)
    {
        _agent = agent;
        _user = user;

        View = CollectionViewSource.GetDefaultView(Facilities);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FacilityViewModel.Customer)));
        View.SortDescriptions.Add(new SortDescription(nameof(FacilityViewModel.Customer), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(FacilityViewModel.Name), ListSortDirection.Ascending));
        View.Filter = item => item is FacilityViewModel f && (!_favouritesOnly || f.IsFavourite) && f.Matches(_search.Trim());

        ConnectCommand = new AsyncRelayCommand(p => ConnectAsync(p as FacilityViewModel ?? Selected));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        ToggleFavouriteCommand = new RelayCommand(p => ToggleFavourite(p as FacilityViewModel ?? Selected));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenLinkCommand = new RelayCommand(p => Try(() => Launcher.Open(((FacilityLink)p!).Url)));
        OpenGatewayDesktopCommand = new RelayCommand(() => Try(() => Launcher.RemoteDesktop(GatewayHost)));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        UpdateCommand = new AsyncRelayCommand(UpdateAsync);

        _timer.Tick += async (_, _) => await PollAsync();
    }

    public ObservableCollection<FacilityViewModel> Facilities { get; } = [];

    /// <summary>The facilities as the list shows them: grouped, sorted and filtered.</summary>
    public ICollectionView View { get; }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand ToggleFavouriteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand OpenGatewayDesktopCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Installs the newer version found, or else looks for one.</summary>
    public AsyncRelayCommand UpdateCommand { get; }

    /// <summary>The agent is about to install an update, which replaces this app too.</summary>
    public event Action? UpdateInstalling;

    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) View.Refresh();
        }
    }

    public bool FavouritesOnly
    {
        get => _favouritesOnly;
        set
        {
            if (!SetProperty(ref _favouritesOnly, value)) return;
            OnPropertyChanged(nameof(ShowAll));
            View.Refresh();
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

    public FacilityViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) OnPropertyChanged(string.Empty);
        }
    }

    // ---------------------------------------------------------------- the connection

    public bool AgentOnline => _agentOnline;
    public bool Configured => _status?.Configured ?? false;
    public bool ShowSetup => _agentOnline && !Configured;
    public ConnectionState State => _status?.State ?? ConnectionState.Disconnected;
    public FacilityViewModel? Active => Find(_status?.FacilityId);

    /// <summary>There is a connection to end.</summary>
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;

    /// <summary>The last thing that went wrong in the app itself (the agent's own troubles are in its message).</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>What this PC and the gateway did lately, newest first.</summary>
    public IReadOnlyList<LogLine> Log => [.. (_status?.Log ?? []).Reverse().Take(30)];

    // ---------------------------------------------------------------- the selected facility

    public bool HasSelection => _selected is not null;
    public bool SelectedIsActive => _selected is not null && _selected == Active;
    public ConnectionState SelectedState => SelectedIsActive ? State : ConnectionState.Disconnected;
    public bool CanConnect => _selected is not null && !(SelectedIsActive && IsBusy);
    public bool CanDisconnect => SelectedIsActive && IsBusy;

    /// <summary>While connecting, a VPN client on the gateway may be waiting for someone to sign in.</summary>
    public bool CanOpenGatewayDesktop => SelectedIsActive && State == ConnectionState.Connecting;

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

    private string GatewayHost => _settings?.Gateway is { } gateway && Probe.TrySplit(gateway, out var host, out _) ? host : _settings?.Gateway ?? "";

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
        _ => "EasyVPN",
    };

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

            foreach (var facility in Facilities)
                facility.State = facility == Active && State != ConnectionState.Disconnected ? State : null;
            OnPropertyChanged(string.Empty);
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
            Selected = Find(selected) ?? Active ?? View.Cast<FacilityViewModel>().FirstOrDefault();
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
        await Try(() => _agent.ConnectAsync(facility.Id));
        await PollAsync();
    }

    private async Task DisconnectAsync()
    {
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
        if (_favouritesOnly) View.Refresh();
        OnPropertyChanged(string.Empty);
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
