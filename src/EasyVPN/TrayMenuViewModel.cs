using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace EasyVPN;

/// <summary>The facility connected, as the tray menu shows it first: its state, what it is doing, and a way to end it.</summary>
public sealed class ConnectedRow(FacilityViewModel facility) : ListRow
{
    private string _state = "";
    private string? _message;
    private bool _canDisconnect;

    public FacilityViewModel Facility { get; } = facility;

    public string StateText
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    /// <summary>What the gateway says it is doing: the latest step while connecting, how it ended.</summary>
    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool CanDisconnect
    {
        get => _canDisconnect;
        set => SetProperty(ref _canDisconnect, value);
    }
}

/// <summary>
/// The tray menu, as DynatecRDM's quick launch: a search, and under it the facility connected,
/// this user's favourites and the facilities they used lately, then every facility by group -
/// closed groups say how many they hold, so a hundred Tosibox Locks take a line. Typing shows
/// every facility that matches instead, wherever it is. The window's model keeps the facilities
/// and the connection up to date; this acts through it.
/// </summary>
public sealed class TrayMenuViewModel : ObservableObject
{
    private const int RecentShown = 5;
    private const int SearchShown = 50;

    private readonly MainViewModel _main;
    private readonly Func<FacilityViewModel, FacilityViewModel?, bool> _confirmSwitch;
    private string _search = "";
    private ListRow? _selected;
    private bool _visible;
    private bool _rebuildQueued;

    // Rows are kept from one build to the next, so the list keeps its place; a facility can be in
    // several sections, as a row of each.
    private readonly Dictionary<string, FacilityRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupRow> _groups = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, ConnectedRow> _connectedRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly SectionRow _connected = new("CONNECTED");
    private readonly SectionRow _failed = new("COULD NOT CONNECT");
    private readonly SectionRow _favourites = new("FAVOURITES");
    private readonly SectionRow _recent = new("RECENT");
    private readonly SectionRow _all = new("ALL FACILITIES");
    private readonly SectionRow _results = new("FOUND");
    private readonly MessageRow _message = new();
    private readonly MessageRow _problem = new();
    private readonly MessageRow _more = new();

    /// <param name="confirmSwitch">Asked before connecting a facility while the gateway carries another (given, or null for none).</param>
    public TrayMenuViewModel(MainViewModel main, Func<FacilityViewModel, FacilityViewModel?, bool> confirmSwitch)
    {
        _main = main;
        _confirmSwitch = confirmSwitch;
        ToggleGroupCommand = new RelayCommand(p =>
        {
            if (p is GroupRow group) Toggle(group, !group.IsOpen);
        });
        DisconnectCommand = new RelayCommand(() => _main.DisconnectCommand.Execute(null));
        ConnectCommand = new RelayCommand(p =>
        {
            if (p is FacilityRow { Facility: var facility }) Connect(facility);
        });
        // A poll changes many things at once: the rows are built again once it is done.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _main.PropertyChanged += (_, _) =>
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _rebuildQueued = false;
                if (_visible) Rebuild();
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(SummaryLevel));
            });
        };
    }

    public ObservableCollection<ListRow> Rows { get; } = [];

    public RelayCommand ToggleGroupCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    /// <summary>A facility row's Connect button: the only way a click connects.</summary>
    public RelayCommand ConnectCommand { get; }

    /// <summary>The popup should go: a facility was chosen.</summary>
    public event Action? CloseRequested;

    /// <summary>The rows changed, so the popup fits its height to them.</summary>
    public event Action? RowsChanged;

    /// <summary>Asked to show a facility in the window.</summary>
    public event Action<FacilityViewModel>? ShowRequested;

    public string SearchText
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            Rebuild();
            SelectFirst();
        }
    }

    public ListRow? SelectedRow
    {
        get => _selected;
        set => SetProperty(ref _selected, value is null or { IsSelectable: true } ? value : _selected);
    }

    /// <summary>The connection in a few words, beside the name at the top.</summary>
    public string Summary => !_main.AgentOnline ? "Agent not running"
        : !_main.Configured ? "No gateway chosen"
        : !_main.GatewayOnline ? "Gateway not answering"
        : _main.Active is { } active && _main.State != ConnectionState.Disconnected ? $"{StateWord(_main.State)}: {active.Name}"
        : "Not connected";

    /// <summary>How the summary is coloured: Ok while connected, Problem when the gateway cannot be reached or a connect failed.</summary>
    public CheckLevel? SummaryLevel => !_main.AgentOnline || !_main.Configured || !_main.GatewayOnline || _main.State == ConnectionState.Failed ? CheckLevel.Problem
        : _main.State == ConnectionState.Connected ? CheckLevel.Ok
        : _main.State is ConnectionState.Connecting or ConnectionState.Interrupted or ConnectionState.Disconnecting ? CheckLevel.Warning
        : null;

    private static string StateWord(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Connected => "Connected",
        ConnectionState.Interrupted => "Interrupted",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Failed => "Failed",
        _ => "",
    };

    /// <summary>Just before the popup shows: an empty search, and the first row chosen.</summary>
    public void PrepareForShow()
    {
        _visible = true;
        _search = "";
        OnPropertyChanged(nameof(SearchText));
        Rebuild();
        SelectFirst();
    }

    public void OnHidden() => _visible = false;

    /// <summary>
    /// A row clicked: a group opens or closes, the one connected shows in the window, and a facility
    /// is only chosen - connecting it takes its Connect button, so a click in passing connects nothing.
    /// </summary>
    public void Click(ListRow row)
    {
        if (row is FacilityRow) SelectedRow = row;
        else Activate(row);
    }

    /// <summary>Enter pressed on a row: a facility connects (the row shows its Connect button), the one connected shows in the window, a group opens or closes.</summary>
    public void Activate(ListRow? row)
    {
        switch (row)
        {
            case GroupRow group:
                Toggle(group, !group.IsOpen);
                break;
            case ConnectedRow connected:
                CloseRequested?.Invoke();
                ShowRequested?.Invoke(connected.Facility);
                break;
            case FacilityRow { Facility: var facility }:
                Connect(facility);
                break;
        }
    }

    public void ActivateSelected() => Activate(_selected);

    /// <summary>Opens (or, given false, closes) the group chosen; closing one inside it chooses the group.</summary>
    public void OpenSelected(bool open)
    {
        if (_selected is GroupRow group)
        {
            if (group.IsOpen != open) Toggle(group, open);
            return;
        }
        if (open || _selected is not FacilityRow { Depth: > 0 } row) return;
        // Left on a facility in a group: up to its group.
        var index = Rows.IndexOf(row);
        for (var i = index - 1; i >= 0; i--)
        {
            if (Rows[i] is GroupRow parent && parent.Depth < row.Depth)
            {
                SelectedRow = parent;
                return;
            }
        }
    }

    /// <summary>Ends the connection, when the row chosen is the one connected. False when it is not.</summary>
    public bool DisconnectSelected()
    {
        var facility = _selected switch
        {
            ConnectedRow connected => connected.Facility,
            FacilityRow row => row.Facility,
            _ => null,
        };
        if (facility is null || !facility.IsActive || !_main.IsBusy) return false;
        _main.DisconnectCommand.Execute(null);
        return true;
    }

    /// <summary>Moves the choice up or down the rows, past titles and messages: a row at a time round the ends, a page at a time to them.</summary>
    public void MoveSelection(int step)
    {
        var selectable = Rows.Where(r => r.IsSelectable).ToList();
        if (selectable.Count == 0) return;
        var at = _selected is null ? -1 : selectable.IndexOf(_selected);
        var next = at < 0 ? (step > 0 ? 0 : selectable.Count - 1)
            : Math.Abs(step) == 1 ? (at + step + selectable.Count) % selectable.Count
            : Math.Clamp(at + step, 0, selectable.Count - 1);
        SelectedRow = selectable[next];
    }

    /// <summary>The first row; while searching, the first facility found, so Enter connects it.</summary>
    private void SelectFirst() => SelectedRow =
        (_search.Trim().Length > 0 ? Rows.FirstOrDefault(r => r is FacilityRow) : null)
        ?? Rows.FirstOrDefault(r => r.IsSelectable);

    private void Connect(FacilityViewModel facility)
    {
        if (facility.IsActive && _main.IsBusy)
        {
            CloseRequested?.Invoke();
            ShowRequested?.Invoke(facility);
            return;
        }
        CloseRequested?.Invoke();
        var carried = _main.IsBusy ? _main.Active : null;
        if (carried is not null && !_confirmSwitch(facility, carried)) return;
        _main.ConnectCommand.Execute(facility);
    }

    private void Toggle(GroupRow group, bool open)
    {
        // A search shows every group open; it is the tree's own state that changes.
        _main.User.SetOpen(group.Path, open);
        _main.RebuildList();
        Rebuild();
        SelectedRow = group;
    }

    // ---------------------------------------------------------------- the rows

    private void Rebuild()
    {
        var query = _search.Trim();
        var rows = new List<ListRow>();
        var facilities = _main.Facilities;

        // What went wrong with the last thing asked - a disconnect the gateway was not told of - first.
        if (_main.Error is { } error)
        {
            _problem.Text = error;
            rows.Add(_problem);
        }

        if (!_main.AgentOnline || !_main.Configured)
        {
            _message.Text = !_main.AgentOnline
                ? "The EasyVPN Agent service is not running on this PC."
                : "Choose a gateway in Settings.";
            rows.Add(_message);
        }
        else if (query.Length > 0)
        {
            // Typing: every facility that matches, under its groups, wherever they are.
            var found = facilities.Where(f => f.Matches(query)).ToList();
            if (found.Count > 0) rows.Add(_results);
            FacilityTree.Append(rows, found, searching: true, _ => true, Group, f => Row("found", f), SearchShown, out var left);
            if (left > 0)
            {
                _more.Text = $"{left} more: type more of the name to find them.";
                rows.Add(_more);
            }
            if (found.Count == 0)
            {
                _message.Text = $"No facility matches \"{query}\".";
                rows.Add(_message);
            }
        }
        else
        {
            // CONNECTED: the facility the gateway carries; or, under a title of its own, the last one
            // that could not be connected, and why.
            var active = _main.State != ConnectionState.Disconnected ? _main.Active : null;
            if (active is not null)
            {
                rows.Add(_main.State == ConnectionState.Failed ? _failed : _connected);
                var row = _connectedRows.TryGetValue(active.Id, out var kept) && kept.Facility == active ? kept : _connectedRows[active.Id] = new ConnectedRow(active);
                row.StateText = StateWord(_main.State);
                row.Message = _main.StatusMessage;
                row.CanDisconnect = _main.IsBusy;
                rows.Add(row);
            }

            var favourites = facilities.Where(f => f.IsFavourite).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (favourites.Count > 0)
            {
                rows.Add(_favourites);
                rows.AddRange(favourites.Select(f => Row("favourite", f)));
            }

            var recent = _main.User.Recent
                .Select(id => facilities.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                .OfType<FacilityViewModel>()
                .Where(f => !f.IsFavourite && f != active)
                .Take(RecentShown)
                .ToList();
            if (recent.Count > 0)
            {
                rows.Add(_recent);
                rows.AddRange(recent.Select(f => Row("recent", f)));
            }

            if (facilities.Count > 0)
            {
                rows.Add(_all);
                var openByDefault = facilities.Count <= FacilityTree.OpenUpTo;
                FacilityTree.Append(rows, facilities, searching: false, path => _main.User.IsOpen(path, openByDefault), Group, f => Row("all", f), int.MaxValue, out _);
            }
            else
            {
                _message.Text = "The gateway has no facilities yet.";
                rows.Add(_message);
            }
        }

        var changed = rows.Count != Rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count)
            {
                if (ReferenceEquals(Rows[i], rows[i])) continue;
                Rows[i] = rows[i];
            }
            else Rows.Add(rows[i]);
            changed = true;
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);

        if (_selected is null || !Rows.Contains(_selected)) SelectFirst();
        if (changed) RowsChanged?.Invoke();
    }

    private FacilityRow Row(string section, FacilityViewModel facility)
    {
        var key = $"{section}/{facility.Id}";
        if (_rows.TryGetValue(key, out var row) && row.Facility == facility) return row;
        return _rows[key] = new FacilityRow(facility);
    }

    private GroupRow Group(string path) => _groups.TryGetValue(path, out var row) ? row : _groups[path] = new GroupRow(path);
}
