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
    private static readonly TimeSpan ChecksEvery = TimeSpan.FromMinutes(5);

    private readonly LocalGateway _gateway;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private GatewayOverview? _overview;
    private bool _serviceOnline = true;
    private string? _error;
    private bool _polling;
    private bool _checking;
    private DateTime _checked = DateTime.MinValue;
    private string _facilities = "";
    private bool _updating;

    public GatewayViewModel(LocalGateway gateway)
    {
        _gateway = gateway;
        ConnectCommand = new AsyncRelayCommand(p => ActAsync(() => _gateway.ConnectAsync(((FacilityRow)p!).Id)));
        DisconnectCommand = new AsyncRelayCommand(() => ActAsync(_gateway.DisconnectAsync));
        CheckCommand = new AsyncRelayCommand(CheckAsync);
        FixCommand = new AsyncRelayCommand(p => FixAsync((CheckRow)p!));
        CopyCommand = new RelayCommand(p => Copy(p as string));
        ShowAccessKeyCommand = new RelayCommand(() => Elevated.Start(Environment.ProcessPath!, "--access-key"));
        EditFacilitiesCommand = new RelayCommand(p => Elevated.Start(Environment.ProcessPath!, p is FacilityRow row ? $"--facilities {row.Id}" : "--facilities"));
        StartServiceCommand = new RelayCommand(() =>
            Elevated.Start("powershell.exe", "-NoProfile -WindowStyle Hidden -Command Start-Service EasyVPN.Gateway", hidden: true));
        UpdateCommand = new AsyncRelayCommand(() => ActAsync(CanInstallUpdate ? _gateway.InstallUpdateAsync : _gateway.CheckUpdatesAsync));
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

    public ObservableCollection<FacilityRow> Facilities { get; } = [];

    /// <summary>What the gateway itself needs: the firewall, Layer 2.</summary>
    public ObservableCollection<CheckRow> SystemChecks { get; } = [];

    public IReadOnlyList<string> FacilityProblems => _overview?.FacilityProblems ?? [];

    // ---------------------------------------------------------------- the service

    public bool ServiceOnline => _serviceOnline;

    public string ServiceText => _serviceOnline ? "Service running" : "Service not running";

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

    /// <summary>The ports workstations reach the gateway on.</summary>
    public string PortsText => $"API {ApiPort} · relay {_overview?.Status.RelayPort ?? Defaults.GatewayRelayPort}";

    // ---------------------------------------------------------------- activity

    /// <summary>
    /// What the gateway did, oldest first. The service keeps only its last lines; the app keeps
    /// every line it has seen since it started, up to <see cref="ActivityLimit"/>.
    /// </summary>
    public ObservableCollection<LogLine> Activity { get; } = [];

    private const int ActivityLimit = 1000;

    public string ActivityText => Activity.Count switch
    {
        0 => "",
        1 => "1 line",
        var n => $"{n} lines",
    };

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

    public string ChecksText => _checking ? "Checking…" : _checked == DateTime.MinValue ? "" : $"Checked at {_checked:HH:mm}";

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
            }
            catch (GatewayException ex)
            {
                _overview = null;
                _serviceOnline = !ex.ServiceDown;
                if (!ex.ServiceDown) Error = ex.Message;
            }
            var changed = Refresh();
            Collect(_overview?.Status.Log ?? []);
            OnPropertyChanged(string.Empty);

            if (_overview?.Update?.State == UpdateState.Installing && !_updating)
            {
                _updating = true;
                UpdateInstalling?.Invoke();
            }

            if (_serviceOnline && (changed || DateTime.Now - _checked > ChecksEvery)) _ = CheckAsync();
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
            facility.Message = carried ? Message : null;
            facility.AskedBy = carried ? AskedBy : null;
        }
        return changed;
    }

    private async Task CheckAsync()
    {
        if (_checking || !_serviceOnline) return;
        _checking = true;
        OnPropertyChanged(string.Empty);
        try
        {
            var results = await _gateway.ChecksAsync();
            SystemChecks.Clear();
            foreach (var result in results.Where(r => r.FacilityId is null)) SystemChecks.Add(new CheckRow(result));
            foreach (var facility in Facilities)
                facility.Readiness = results.FirstOrDefault(r => string.Equals(r.FacilityId, facility.Id, StringComparison.OrdinalIgnoreCase));
            _checked = DateTime.Now;
            Error = null;
        }
        catch (GatewayException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _checking = false;
            OnPropertyChanged(string.Empty);
        }
    }

    private async Task FixAsync(CheckRow check)
    {
        switch (check.Fix)
        {
            case GatewayFix.Layer2Adapter:
                await ActAsync(_gateway.AddLayer2AdapterAsync);
                await CheckAsync();
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

/// <summary>A facility in the list: what it is, whether it is the one connected, and whether its VPN client is ready.</summary>
public sealed class FacilityRow(FacilityInfo info) : ObservableObject
{
    private ConnectionState? _state;
    private CheckResult? _readiness;
    private string? _message;
    private string? _askedBy;

    public FacilityInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;

    public string Details => string.Join("  ·  ", new[]
    {
        Info.Customer,
        Info.Connector,
        Info.Layer2 ? "Layer 2" : Info.Routes.Count == 0 ? "networks from its VPN" : string.Join(", ", Info.Routes),
        Info.Check is { } check ? $"check {check}" : "no check",
    }.Where(s => s.Length > 0));

    /// <summary>Set while this is the facility the gateway carries.</summary>
    public ConnectionState? State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) Raise(nameof(IsCarried));
        }
    }

    /// <summary>The gateway carries it, or is getting to: Disconnect, rather than Connect.</summary>
    public bool IsCarried => _state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Interrupted;

    /// <summary>What the gateway says of it while it carries it: the latest step, or how it ended.</summary>
    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string? AskedBy
    {
        get => _askedBy;
        set => SetProperty(ref _askedBy, value);
    }

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
