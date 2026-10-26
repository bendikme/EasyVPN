using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The window and the tray icon: the connection the gateway carries, what workstations need to
/// reach it, its facilities and whether each is ready, and whatever else it is missing. The
/// connection comes from the service every two seconds; the checks, which run every facility's
/// connector, when the facilities change and every few minutes.
/// </summary>
public sealed class GatewayViewModel : ObservableObject
{
    // The service checks in the background and keeps what it found; this only reads it.
    private static readonly TimeSpan ReadChecksEvery = TimeSpan.FromSeconds(10);

    private readonly LocalGateway _gateway;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private GatewayOverview? _overview;
    private bool _serviceOnline = true;
    private string? _error;
    private string? _pollProblem;
    private bool _polling;
    private bool _checking;
    private DateTime _checksRead = DateTime.MinValue;
    private bool _readingChecks;
    private string _facilities = "";
    private bool _updating;
    private string _search = "";

    // The customers' groups, kept so each keeps its place in the list, and those opened.
    private readonly Dictionary<string, FacilityGroup> _groups = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _open = new(StringComparer.CurrentCultureIgnoreCase);

    public GatewayViewModel(LocalGateway gateway)
    {
        _gateway = gateway;
        ConnectCommand = new AsyncRelayCommand(p => ActAsync(() => _gateway.ConnectAsync(((FacilityRow)p!).Id)));
        DisconnectCommand = new AsyncRelayCommand(() => ActAsync(_gateway.DisconnectAsync));
        CheckCommand = new AsyncRelayCommand(() => ReadChecksAsync(refresh: true));
        FixCommand = new AsyncRelayCommand(p => FixAsync((CheckRow)p!));
        CopyCommand = new RelayCommand(p => Copy(p as string));
        ShowAccessKeyCommand = new RelayCommand(() => Elevated.Start(Environment.ProcessPath!, "--access-key"));
        EditFacilitiesCommand = new RelayCommand(p => Elevated.Start(Environment.ProcessPath!, p is FacilityRow row ? $"--facilities {row.Id}" : "--facilities"));
        StartServiceCommand = new RelayCommand(() =>
            Elevated.Start("powershell.exe", "-NoProfile -WindowStyle Hidden -Command Start-Service EasyVPN.Gateway", hidden: true));
        UpdateCommand = new AsyncRelayCommand(() => ActAsync(CanInstallUpdate ? _gateway.InstallUpdateAsync : _gateway.CheckUpdatesAsync));
        ToggleGroupCommand = new RelayCommand(p =>
        {
            if (p is not FacilityGroup group) return;
            if (!_open.Remove(group.Name)) _open.Add(group.Name);
            Arrange();
        });
        _timer.Tick += async (_, _) => await PollAsync();
    }

    /// <summary>The service is about to install an update, which replaces this app too.</summary>
    public event Action? UpdateInstalling;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand FixCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand ShowAccessKeyCommand { get; }

    /// <summary>The facility editor, as administrator; given a facility, showing that one.</summary>
    public RelayCommand EditFacilitiesCommand { get; }

    /// <summary>Settings, as administrator: what the facilities of a kind share.</summary>
    public RelayCommand SettingsCommand { get; } = new(() => Elevated.Start(Environment.ProcessPath!, "--settings"));
    public RelayCommand StartServiceCommand { get; }

    /// <summary>Installs the newer version found, or else looks for one.</summary>
    public AsyncRelayCommand UpdateCommand { get; }

    /// <summary>Opens or closes a customer's group.</summary>
    public RelayCommand ToggleGroupCommand { get; }

    public ObservableCollection<FacilityRow> Facilities { get; } = [];

    /// <summary>
    /// The list as shown: the facility carried first, under its own title, as in the workstations'
    /// tray menu; then the rest in a group for each customer, which opens and closes - closed until
    /// opened, and while searching, open wherever something matches.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Arrange();
        }
    }

    /// <summary>A search that no customer's facility matches.</summary>
    public bool NoMatches => _search.Trim().Length > 0 && !Rows.OfType<FacilityGroup>().Any();

    public bool HasNoFacilities => _serviceOnline && _overview is not null && Facilities.Count == 0;

    /// <summary>What the gateway itself needs: the firewall, Layer 2.</summary>
    public ObservableCollection<CheckRow> SystemChecks { get; } = [];

    /// <summary>Those of its own checks that are not fine: a banner each.</summary>
    public IReadOnlyList<CheckRow> SystemProblems => [.. SystemChecks.Where(c => c.Level != CheckLevel.Ok)];

    public IReadOnlyList<string> FacilityProblems => _overview?.FacilityProblems ?? [];

    // ---------------------------------------------------------------- the service

    public bool ServiceOnline => _serviceOnline;

    public string ServiceText => _serviceOnline ? "Service running" : "Service not running";

    public CheckLevel ServiceLevel => _serviceOnline ? CheckLevel.Ok : CheckLevel.Problem;

    /// <summary>The last thing that went wrong in the app itself.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    // ---------------------------------------------------------------- the connection

    public ConnectionState State => _overview?.Status.State ?? ConnectionState.Disconnected;

    public FacilityRow? Active =>
        _overview?.Status.FacilityId is { } id ? Facilities.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;

    public string StateText => !_serviceOnline ? "Unknown" : State switch
    {
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Connected => "Connected",
        ConnectionState.Interrupted => "Interrupted",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => "Failed",
        _ => "Not connected",
    };

    public string Headline =>
        !_serviceOnline ? "The EasyVPN Gateway service is not running"
        : State == ConnectionState.Disconnected ? "No facility connected"
        : Active?.Name ?? _overview?.Status.FacilityId ?? "";

    public string? Message => _serviceOnline && State != ConnectionState.Disconnected ? _overview?.Status.Message : null;

    public string? AskedBy => _serviceOnline && State != ConnectionState.Disconnected && _overview?.Status.Client is { } client ? $"Asked by {client}" : null;

    /// <summary>The connection, in a few words, for the status strip.</summary>
    public string ConnectionText => !_serviceOnline ? "Not connected" : State switch
    {
        ConnectionState.Connecting => $"Connecting to {ActiveName}",
        ConnectionState.Connected => $"Connected to {ActiveName}",
        ConnectionState.Interrupted => $"{ActiveName} interrupted",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => $"{ActiveName} failed",
        _ => "No facility connected",
    };

    private string ActiveName => Active?.Name ?? _overview?.Status.FacilityId ?? "the facility";

    // ---------------------------------------------------------------- activity

    /// <summary>
    /// What the gateway did, oldest first. The service keeps only its last lines; the app keeps
    /// every line it has seen since it started, up to <see cref="ActivityLimit"/>.
    /// </summary>
    public ObservableCollection<LogLine> Activity { get; } = [];

    private const int ActivityLimit = 1000;

    /// <summary>Adds what is new in the service's latest lines.</summary>
    private void Collect(IReadOnlyList<LogLine> lines)
    {
        var last = Activity.LastOrDefault();
        foreach (var line in lines)
        {
            // Lines come in order; one stamped with the time of the last one seen may still be new.
            if (last is not null && (line.Time < last.Time || (line.Time == last.Time && Activity.Contains(line)))) continue;
            Activity.Add(line);
        }
        while (Activity.Count > ActivityLimit) Activity.RemoveAt(0);
    }

    // ---------------------------------------------------------------- for workstations

    private int ApiPort => _overview?.ApiPort ?? Defaults.GatewayApiPort;

    private string WithPort(string host) => ApiPort == Defaults.GatewayApiPort ? host : $"{host}:{ApiPort}";

    public string GatewayAddress => WithPort(_overview?.Machine ?? Environment.MachineName);

    public string OtherAddresses => _overview is { Addresses.Count: > 0 } o ? "or " + string.Join(", ", o.Addresses.Select(WithPort)) : "";

    public string Thumbprint => _overview?.Thumbprint ?? "";

    // ---------------------------------------------------------------- updates

    public UpdateStatus? Update => _overview?.Update;

    public bool CanInstallUpdate => Update is { State: UpdateState.Available or UpdateState.Failed, Latest: not null } update
        && Version.TryParse(update.Latest, out var latest) && Version.TryParse(update.Current, out var current) && latest > current;

    public string UpdateText => Update switch
    {
        null => "",
        { State: UpdateState.Checking } => "Looking for updates…",
        { State: UpdateState.Available, Automatic: true } u => $"EasyVPN {u.Latest} is available. It installs by itself when no facility is connected.",
        { State: UpdateState.Available } u => $"EasyVPN {u.Latest} is available.",
        { State: UpdateState.Downloading } u => $"Downloading EasyVPN {u.Latest}…",
        { State: UpdateState.Installing } u => $"Installing EasyVPN {u.Latest}: the gateway and this app restart.",
        { State: UpdateState.Failed } u => u.Message ?? "The update failed.",
        { State: UpdateState.UpToDate } => "Up to date.",
        var u => u.Message ?? "",
    };

    public string VersionText => Update is { } u ? $"Version {u.Current}" : "";

    /// <summary>The update, in the status strip, when there is something to say: nothing when up to date.</summary>
    public string UpdateNote => Update?.State == UpdateState.UpToDate ? "" : UpdateText;

    public string UpdateButtonText => CanInstallUpdate ? $"Install {Update!.Latest}" : "Check for updates";

    public bool CanUpdate => _serviceOnline && Update is { State: not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) };

    public CheckLevel UpdateLevel => Update?.State switch
    {
        UpdateState.UpToDate => CheckLevel.Ok,
        UpdateState.Failed => CheckLevel.Problem,
        _ => CheckLevel.Warning,
    };

    // ---------------------------------------------------------------- checks

    public bool Checking => _checking;

    public string RefreshText => _checking ? "Checking…" : "Refresh";

    public int ProblemCount =>
        SystemChecks.Count(c => c.Level == CheckLevel.Problem)
        + Facilities.Count(f => f.Level == CheckLevel.Problem)
        + FacilityProblems.Count;

    public string ProblemText => ProblemCount switch
    {
        0 => "",
        1 => "1 thing needs attention",
        var n => $"{n} things need attention",
    };

    /// <summary>The tray icon's colour, as on the workstations: grey without the service, amber while connecting, green connected, red when it went wrong.</summary>
    public TrayLook TrayLook => State switch
    {
        _ when !_serviceOnline => TrayLook.Off,
        ConnectionState.Connecting or ConnectionState.Disconnecting => TrayLook.Busy,
        ConnectionState.Connected => TrayLook.Connected,
        ConnectionState.Interrupted or ConnectionState.Failed => TrayLook.Problem,
        _ => TrayLook.Idle,
    };

    public string TrayText => State switch
    {
        _ when !_serviceOnline => "EasyVPN Gateway: the service is not running",
        ConnectionState.Connected => $"EasyVPN Gateway: connected to {Active?.Name}",
        ConnectionState.Connecting => $"EasyVPN Gateway: connecting to {Active?.Name}",
        ConnectionState.Interrupted => $"EasyVPN Gateway: {Active?.Name} interrupted",
        ConnectionState.Failed => "EasyVPN Gateway: the last connection failed",
        _ when ProblemCount > 0 => $"EasyVPN Gateway: {ProblemText}",
        _ => "EasyVPN Gateway",
    };

    // ---------------------------------------------------------------- work

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            try
            {
                _overview = await _gateway.OverviewAsync();
                _serviceOnline = true;
                // What a slow answer said is over once one comes in time.
                if (Error is not null && Error == _pollProblem) Error = null;
                _pollProblem = null;
            }
            catch (GatewayException ex) when (ex.ServiceDown)
            {
                _overview = null;
                _serviceOnline = false;
            }
            catch (GatewayException ex)
            {
                // There, and slow: what it showed last stays.
                _serviceOnline = true;
                Error = _pollProblem = ex.Message;
            }
            var changed = Refresh();
            Collect(_overview?.Status.Log ?? []);
            OnPropertyChanged(string.Empty);

            if (_overview?.Update?.State == UpdateState.Installing && !_updating)
            {
                _updating = true;
                UpdateInstalling?.Invoke();
            }

            if (_serviceOnline && (changed || _checking || DateTime.Now - _checksRead > ReadChecksEvery)) _ = ReadChecksAsync(refresh: false);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>The facility list follows the service's, keeping each one's last check.</summary>
    /// <returns>True when the facilities changed, and so are worth checking again.</returns>
    private bool Refresh()
    {
        var facilities = _overview?.Facilities ?? [];
        var json = JsonSerializer.Serialize(facilities, Defaults.Json);
        var changed = json != _facilities;
        if (changed)
        {
            var checks = Facilities.ToDictionary(f => f.Id, f => f.Readiness, StringComparer.OrdinalIgnoreCase);
            Facilities.Clear();
            foreach (var info in facilities.OrderBy(f => f.Customer).ThenBy(f => f.Name))
                Facilities.Add(new FacilityRow(info) { Readiness = checks.GetValueOrDefault(info.Id) });
            // Last: a list that could not be shown is tried again on the next poll.
            _facilities = json;
        }
        var active = Active;
        foreach (var facility in Facilities)
        {
            var carried = facility == active && State != ConnectionState.Disconnected;
            facility.State = carried ? State : null;
            // "Connected to …" says no more than its state does.
            facility.Message = carried && !(State == ConnectionState.Connected && Message == $"Connected to {facility.Name}") ? Message : null;
            facility.AskedBy = carried ? AskedBy : null;
        }
        Arrange();
        return changed;
    }

    /// <summary>Lays out <see cref="Rows"/> again, leaving it be when nothing moved.</summary>
    private void Arrange()
    {
        var carried = Facilities.FirstOrDefault(f => f.State is not null);
        var terms = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new List<object>();
        if (carried is not null)
        {
            rows.Add(new ListSection(carried.State == ConnectionState.Failed ? "COULD NOT CONNECT" : "CONNECTED"));
            rows.Add(carried);
            carried.InGroup = false;
        }
        var customers = Facilities.Where(f => f != carried && terms.All(f.Matches))
            .GroupBy(f => string.IsNullOrWhiteSpace(f.Info.Customer) ? "Other" : f.Info.Customer.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (carried is not null && customers.Count > 0) rows.Add(new ListSection("ALL FACILITIES"));
        foreach (var customer in customers)
        {
            if (!_groups.TryGetValue(customer.Key, out var group)) _groups[customer.Key] = group = new FacilityGroup(customer.Key);
            group.Count = customer.Count();
            group.NotReady = customer.Count(f => f.Level is CheckLevel.Warning or CheckLevel.Problem);
            group.IsOpen = terms.Length > 0 || _open.Contains(customer.Key);
            rows.Add(group);
            if (!group.IsOpen) continue;
            foreach (var facility in customer) facility.InGroup = true;
            rows.AddRange(customer);
        }
        // In place, so a group opening or closing leaves the list where it was scrolled to.
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(rows[i]);
            else if (!Equals(Rows[i], rows[i])) Rows[i] = rows[i];
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
        OnPropertyChanged(nameof(NoMatches));
    }

    /// <summary>
    /// What the service has found: it checks in the background, at its own pace, and keeps it. This
    /// only reads it - which takes no time - or, with <paramref name="refresh"/> (the Refresh button),
    /// asks it to check everything again.
    /// </summary>
    private async Task ReadChecksAsync(bool refresh)
    {
        if (_readingChecks || !_serviceOnline) return;
        _readingChecks = true;
        try
        {
            var run = await _gateway.ChecksAsync(refresh);
            _checksRead = DateTime.Now;
            SystemChecks.Clear();
            foreach (var result in run.Results.Where(r => r.FacilityId is null)) SystemChecks.Add(new CheckRow(result));
            foreach (var facility in Facilities)
                facility.Readiness = run.Results.FirstOrDefault(r => string.Equals(r.FacilityId, facility.Id, StringComparison.OrdinalIgnoreCase));
            _checking = run.Running || refresh;
        }
        catch (GatewayException ex) when (!ex.ServiceDown)
        {
            Error = ex.Message;
        }
        catch (GatewayException)
        {
            // The service went: the status bar says so.
        }
        finally
        {
            _readingChecks = false;
            OnPropertyChanged(string.Empty);
        }
    }

    private async Task FixAsync(CheckRow check)
    {
        switch (check.Fix)
        {
            case GatewayFix.Layer2Adapter:
                await ActAsync(_gateway.AddLayer2AdapterAsync);
                await ReadChecksAsync(refresh: true);
                break;
            case GatewayFix.Bridge:
                Elevated.Open("ncpa.cpl");
                break;
            case { } page:
                Elevated.Open(page);
                break;
        }
    }

    private async Task ActAsync(Func<Task> action)
    {
        try
        {
            await action();
            Error = null;
        }
        catch (GatewayException ex)
        {
            Error = ex.Message;
        }
        await PollAsync();
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; a second click will do.
        }
    }
}

/// <summary>A title in the facility list: the connection, or all the rest.</summary>
public sealed record ListSection(string Title)
{
    public bool IsSelectable => false;
    public bool IsGroup => false;
}

/// <summary>A customer's facilities in the list: a row of its own, which opens and closes them.</summary>
public sealed class FacilityGroup(string name) : ObservableObject
{
    private int _count;
    private int _notReady;
    private bool _open;

    public string Name { get; } = name;
    public bool IsSelectable => true;
    public bool IsGroup => true;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsOpen
    {
        get => _open;
        set => SetProperty(ref _open, value);
    }

    /// <summary>Its facilities whose check found something: said on the group, so a closed one hides none.</summary>
    public int NotReady
    {
        get => _notReady;
        set
        {
            if (SetProperty(ref _notReady, value)) OnPropertyChanged(nameof(NotReadyText));
        }
    }

    public string? NotReadyText => _notReady == 0 ? null : $"{_notReady} not ready";
}

/// <summary>A facility in the list: what it is, whether it is the one connected, and whether its VPN client is ready.</summary>
public sealed class FacilityRow(FacilityInfo info) : ObservableObject
{
    private ConnectionState? _state;
    private CheckResult? _readiness;
    private bool _inGroup;
    private string? _message;
    private string? _askedBy;

    public FacilityInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;

    public bool IsSelectable => true;
    public bool IsGroup => false;

    /// <summary>In its customer's group, indented under it; not when it is the one carried, on top.</summary>
    public bool InGroup
    {
        get => _inGroup;
        set => SetProperty(ref _inGroup, value);
    }

    /// <summary>What it is, under its name: its customer is the group it is listed in.</summary>
    public string Details => string.Join("  ·  ", new[]
    {
        Info.Connector,
        Info.Layer2 ? "Layer 2" : string.Join(", ", Info.Routes),
        Info.Check is { } check ? $"check {check}" : "",
    }.Where(s => s.Length > 0));

    /// <summary>Whether a word of a search is in its name, customer, VPN client, networks or notes.</summary>
    public bool Matches(string term) =>
        new[] { Info.Name, Info.Customer, Info.Connector, Info.Notes ?? "", string.Join(" ", Info.Routes) }
            .Any(text => text.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Set while this is the facility the gateway carries.</summary>
    public ConnectionState? State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) Raise(nameof(IsCarried), nameof(IsLeaving), nameof(IsFree), nameof(StateText));
        }
    }

    public string? StateText => _state switch
    {
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Connected => "Connected",
        ConnectionState.Interrupted => "Interrupted",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => "Failed",
        _ => null,
    };

    /// <summary>The gateway carries it, or is getting to: Disconnect, rather than Connect.</summary>
    public bool IsCarried => _state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;

    /// <summary>On its way down, which can take a VPN client a minute: neither Connect nor Disconnect, but saying so.</summary>
    public bool IsLeaving => _state is ConnectionState.Disconnecting;

    /// <summary>Neither carried nor on its way down: Connect.</summary>
    public bool IsFree => !IsCarried && !IsLeaving;

    /// <summary>What the gateway says of it while it carries it: the latest step, or how it ended.</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value)) Raise(nameof(Doing));
        }
    }

    public string? AskedBy
    {
        get => _askedBy;
        set
        {
            if (SetProperty(ref _askedBy, value)) Raise(nameof(Doing));
        }
    }

    /// <summary>The latest step, and who asked for it, on one line.</summary>
    public string? Doing => string.Join("  ·  ", new[] { _message, _askedBy }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } text ? text : null;

    public CheckResult? Readiness
    {
        get => _readiness;
        set
        {
            if (SetProperty(ref _readiness, value)) Raise(nameof(Level), nameof(ReadinessText), nameof(Attention));
        }
    }

    public CheckLevel? Level => _readiness?.Level;

    public string ReadinessText => _readiness?.Text ?? "Not checked yet";

    /// <summary>What its check found, shown only when it is not ready: a ready one says so in its dot's tooltip.</summary>
    public string? Attention => _readiness is { Level: not CheckLevel.Ok } ? _readiness.Text : null;
}

/// <summary>One of the gateway's own checks, and what the app can do about it.</summary>
public sealed class CheckRow(CheckResult result)
{
    public string Subject => result.Subject;
    public CheckLevel Level => result.Level;
    public string Text => result.Text;
    public string? Fix => result.Fix;

    public string? FixText => result.Fix switch
    {
        null => null,
        GatewayFix.Layer2Adapter => "Add the adapter",
        GatewayFix.Bridge => "Open Network Connections",
        GatewayFix.OpenVpnDownload => "Download OpenVPN",
        _ => "Open",
    };
}
