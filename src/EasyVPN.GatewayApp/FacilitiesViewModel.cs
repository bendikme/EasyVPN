using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Data;
using EasyVPN.Gateway;
using Microsoft.Win32;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The facility editor: facilities.json as a form. It runs in a copy of the app started as
/// administrator, the only kind of account that can read or write the file, and writes the file
/// whole; the gateway reads it again as soon as it changes. Facilities nobody touched are written
/// back as they were read.
/// </summary>
public sealed class FacilitiesViewModel : ObservableObject
{
    // Readable by hand, Ålesund and all.
    private static readonly JsonSerializerOptions FileJson = new(Defaults.Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _folder;
    private readonly string _path;
    private readonly Func<string, bool> _ask;
    private DateTime _stamp;
    private string _saved = "";
    private bool _canEdit;
    private FacilityDraft? _selected;
    private string _filter = "";
    private string? _status;
    private string? _error;

    /// <param name="select">The id of the facility to show first.</param>
    /// <param name="ask">Asks the person a yes-or-no question.</param>
    public FacilitiesViewModel(string? select, Func<string, bool> ask)
    {
        _ask = ask;
        _folder = DataDirectory.Get("Gateway");
        _path = Path.Combine(_folder, "facilities.json");
        foreach (var kind in ConnectorKinds.Available(_folder)) Kinds.Add(kind);

        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Facilities);
        View.SortDescriptions.Add(new SortDescription(nameof(FacilityDraft.Customer), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(FacilityDraft.Title), ListSortDirection.Ascending));
        View.IsLiveSorting = true;
        View.LiveSortingProperties.Add(nameof(FacilityDraft.Customer));
        View.LiveSortingProperties.Add(nameof(FacilityDraft.Title));
        View.Filter = item => Matches((FacilityDraft)item);

        AddCommand = new RelayCommand(Add, () => _canEdit);
        DuplicateCommand = new RelayCommand(Duplicate, () => _canEdit && _selected is not null);
        RemoveCommand = new RelayCommand(Remove, () => _canEdit && _selected is not null);
        SaveCommand = new RelayCommand(() => Save(), () => _canEdit);
        BrowseCommand = new RelayCommand(p => Browse((SettingRow)p!));
        OpenFileCommand = new RelayCommand(() => Process.Start("notepad.exe", $"\"{_path}\"")?.Dispose());

        Load(select);
    }

    public ObservableCollection<FacilityDraft> Facilities { get; } = [];

    /// <summary>The list as shown: by customer, then name, and only what matches <see cref="Filter"/>.</summary>
    public ListCollectionView View { get; }

    public ObservableCollection<ConnectorKind> Kinds { get; } = [];

    public RelayCommand AddCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public string FilePath => _path;

    public FacilityDraft? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            DuplicateCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) View.Refresh();
        }
    }

    public string CountText => Facilities.Count switch
    {
        0 => "None yet",
        1 => "1 facility",
        var n => $"{n} facilities",
    };

    /// <summary>What was done last: saved, a file copied.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Why the file cannot be edited here, or why it was not saved.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Changed here and not saved yet.</summary>
    public bool IsDirty => _canEdit && Serialize(protect: false) != _saved;

    // ---------------------------------------------------------------- the file

    private void Load(string? select)
    {
        foreach (var draft in Facilities) draft.PropertyChanged -= OnDraftChanged;
        Facilities.Clear();
        _canEdit = false;
        try
        {
            _stamp = File.GetLastWriteTimeUtc(_path);
            var file = DataDirectory.ReadJson<FacilityFile>(_path);
            foreach (var facility in file?.Facilities ?? []) Track(new FacilityDraft(facility, KindOf(facility.Connector)));
            _saved = Serialize(protect: false);
            _canEdit = true;
            Error = null;
        }
        catch (UnauthorizedAccessException)
        {
            Error = "It takes an administrator to change the facilities.";
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Error = $"facilities.json could not be read, so nothing is changed here: {ex.Message} Open the file to put it right; it is loaded again when you come back.";
        }

        Validate();
        Selected = Facilities.FirstOrDefault(f => string.Equals(f.Id, select, StringComparison.OrdinalIgnoreCase))
            ?? View.Cast<FacilityDraft>().FirstOrDefault();
        foreach (var command in new[] { AddCommand, DuplicateCommand, RemoveCommand, SaveCommand }) command.RaiseCanExecuteChanged();
        Raise(nameof(CountText));
    }

    /// <summary>The file was changed elsewhere (in Notepad, say) and nothing here waits to be saved: show what it says now.</summary>
    public void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(_path) || File.GetLastWriteTimeUtc(_path) == _stamp || IsDirty) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Load(_selected?.Id);
        if (_canEdit) Status = "facilities.json changed elsewhere, so it was loaded again.";
    }

    /// <returns>False when it was not saved: the person said no, or it could not be written.</returns>
    public bool Save()
    {
        if (!_canEdit) return false;
        Validate();
        if ((_selected is { Duplicate: true } ? _selected : Facilities.FirstOrDefault(f => f.Duplicate)) is { } twin)
        {
            Selected = twin;
            Error = $"Two facilities have the id '{twin.Id}': each needs its own.";
            return false;
        }
        var broken = Facilities.Count(f => f.Problem is not null);
        if (broken > 0 && !_ask(broken == 1
                ? "One facility has a problem, shown in red. The gateway leaves it out until it is put right. Save anyway?"
                : $"{broken} facilities have problems, shown in red. The gateway leaves them out until they are put right. Save anyway?"))
            return false;
        if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _stamp && !_ask(
                "facilities.json has changed since it was opened here, in Notepad perhaps. Save over those changes?"))
            return false;

        try
        {
            var facilities = Facilities.Select(f => (Draft: f, Facility: f.ToFacility(protect: true))).ToList();
            Write(new FacilityFile([.. facilities.Select(f => f.Facility)]));
            foreach (var (draft, facility) in facilities) draft.Accept(facility.Password);
            _stamp = File.GetLastWriteTimeUtc(_path);
            _saved = Serialize(protect: false);
            Error = null;
            Status = $"Saved at {DateTime.Now:HH:mm}. The gateway uses it straight away.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"facilities.json could not be saved: {ex.Message}";
            return false;
        }
    }

    /// <summary>Writes beside the file and swaps it in, leaving out what is empty or the default, so the file stays short.</summary>
    private void Write(FacilityFile file)
    {
        var json = JsonSerializer.SerializeToNode(file, FileJson)!;
        foreach (var facility in json["facilities"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var key in (string[])["settings", "routes", "links"])
                if (facility[key] is JsonObject { Count: 0 } or JsonArray { Count: 0 }) facility.Remove(key);
            if (facility["layer2"]?.GetValue<bool>() == false) facility.Remove("layer2");
            if (facility["timeout"]?.GetValue<int>() == Facility.DefaultTimeout) facility.Remove("timeout");
        }
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(FileJson));
        File.Move(temp, _path, overwrite: true);
    }

    private string Serialize(bool protect) =>
        JsonSerializer.Serialize(Facilities.Select(f => f.ToFacility(protect)), FileJson);

    // ---------------------------------------------------------------- the list

    private void Add()
    {
        var draft = new FacilityDraft(new Facility { Customer = _selected?.Customer ?? "" }, KindOf("manual")) { AutoId = true };
        Track(draft);
        Filter = "";
        Selected = draft;
    }

    private void Duplicate()
    {
        if (_selected is not { } original) return;
        var copy = original.ToFacility(protect: true);
        copy.Name += " (copy)";
        var draft = new FacilityDraft(copy, original.Kind) { AutoId = true };
        Track(draft);
        Selected = draft;
    }

    private void Remove()
    {
        if (_selected is not { } draft) return;
        var shown = View.Cast<FacilityDraft>().ToList();
        var index = shown.IndexOf(draft);
        draft.PropertyChanged -= OnDraftChanged;
        Facilities.Remove(draft);
        shown.Remove(draft);
        Selected = shown.Count == 0 ? null : shown[Math.Clamp(index, 0, shown.Count - 1)];
        Validate();
        Raise(nameof(CountText));
    }

    private void Track(FacilityDraft draft)
    {
        Facilities.Add(draft);
        draft.PropertyChanged += OnDraftChanged;
        if (draft.AutoId) MakeId(draft);
        Validate();
        Raise(nameof(CountText));
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        var draft = (FacilityDraft)sender!;
        // Validate's own results.
        if (e.PropertyName is nameof(FacilityDraft.Problem) or nameof(FacilityDraft.ProblemText) or nameof(FacilityDraft.HasProblem)
            or nameof(FacilityDraft.Duplicate)) return;
        if (draft.AutoId && e.PropertyName is nameof(FacilityDraft.Name) or nameof(FacilityDraft.Customer)) MakeId(draft);
        Validate();
        Status = null;
    }

    private bool Matches(FacilityDraft draft) =>
        _filter.Length == 0
        || draft.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || draft.Customer.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || draft.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private ConnectorKind KindOf(string name)
    {
        if (Kinds.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) is { } kind) return kind;
        // A connector that is not here (any more): kept, so the facility does not change by being opened.
        var custom = ConnectorKind.Custom(name);
        Kinds.Add(custom);
        return custom;
    }

    /// <summary>A new facility's id follows its customer and name until someone types one: "aqua-nord-hitra".</summary>
    private void MakeId(FacilityDraft draft)
    {
        var stem = Slug($"{draft.Customer} {draft.Name}");
        if (stem.Length == 0) stem = "facility";
        var id = stem;
        for (var n = 2; Facilities.Any(f => f != draft && string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)); n++) id = $"{stem}-{n}";
        draft.SetAutoId(id);
    }

    private static string Slug(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Replace("æ", "ae").Replace("ø", "o").Replace("å", "a").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        var slug = plain.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    /// <summary>Each facility's problem, including an id another one has too.</summary>
    private void Validate()
    {
        var ids = Facilities.GroupBy(f => f.Id.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in Facilities)
        {
            draft.Duplicate = ids.Contains(draft.Id.Trim());
            draft.Problem = draft.Duplicate ? "has the same id as another facility" : draft.FindProblem();
        }
    }

    // ---------------------------------------------------------------- files

    /// <summary>
    /// A profile or tunnel file, copied into the gateway's own vpn folder: the connector runs it as
    /// SYSTEM, so it must be somewhere only administrators can change, and stay put. An OpenVPN
    /// profile is also read for its networks, and made fit for OpenVPN 2.6.
    /// </summary>
    private void Browse(SettingRow row)
    {
        var dialog = new OpenFileDialog { Filter = row.Field.Filter ?? "All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;

        // A program installed here is pointed at where it is.
        if (row.Field.Kind == FieldKind.Program)
        {
            row.Value = dialog.FileName;
            return;
        }

        var draft = (FacilityDraft)row.Owner;
        var source = dialog.FileName;
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "vpn")).FullName;
        var target = source;
        var said = new List<string>();
        try
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), folder, StringComparison.OrdinalIgnoreCase))
            {
                // Named after the facility, when its id will do for a file name.
                var id = draft.Id.Trim();
                var name = new Facility { Id = id, Layer2 = true }.Problem() is null ? id : Path.GetFileNameWithoutExtension(source);
                target = Path.Combine(folder, name + Path.GetExtension(source));
                File.Copy(source, target, overwrite: true);
                said.Add($"{Path.GetFileName(source)} copied to {target}.");
            }
            row.Value = target;
            if (draft.Kind.Name == "openvpn") said.AddRange(TakeIn(draft, target));
            Status = string.Join(" ", said);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"{Path.GetFileName(source)} could not be copied: {ex.Message}";
        }
    }

    /// <summary>What an OpenVPN profile tells about its facility, put into the form.</summary>
    private static IEnumerable<string> TakeIn(FacilityDraft draft, string profile)
    {
        var reading = OpenVpnProfile.Adapt(profile);
        foreach (var change in reading.Changes) yield return $"In that copy, {change}.";

        if (reading.Routes.Count == 0)
            yield return "It names no networks, so its server sends them when it connects: leave Routes empty to use those.";
        else if (draft.Routes.Trim().Length == 0)
        {
            draft.Routes = string.Join(Environment.NewLine, reading.Routes);
            yield return $"Routes taken from it: {string.Join(", ", reading.Routes)}.";
        }

        if (reading.OldCertificates && draft.Fields.FirstOrDefault(f => f.Field.Key == "legacy") is { IsChecked: false } legacy)
        {
            legacy.IsChecked = true;
            yield return "Its certificates are old (SHA1, or a short RSA key), which OpenVPN 2.6 refuses unless they are allowed: that is now ticked.";
        }
    }
}

/// <summary>One facility as the editor holds it: the form's fields, turned back into a <see cref="Facility"/> to save.</summary>
public sealed class FacilityDraft : ObservableObject, ISettingsOwner
{
    private string _id;
    private string _name;
    private string _customer;
    private ConnectorKind _kind;
    private readonly Dictionary<string, string> _settings;
    private string? _password;
    private string _newPassword = "";
    private bool _removePassword;
    private bool _layer2;
    private string _routes;
    private string _dnsDomains;
    private string _dnsServers;
    private string _check;
    private string _timeout;
    private string _notes;
    private string _links;
    private string? _problem;
    private bool _duplicate;

    public FacilityDraft(Facility facility, ConnectorKind kind)
    {
        _id = facility.Id;
        _name = facility.Name;
        _customer = facility.Customer;
        _kind = kind;
        _settings = new Dictionary<string, string>(facility.Settings, StringComparer.OrdinalIgnoreCase);
        _password = facility.Password;
        _layer2 = facility.Layer2;
        _routes = string.Join(Environment.NewLine, facility.Routes);
        _dnsDomains = string.Join(", ", facility.Dns?.Domains ?? []);
        _dnsServers = string.Join(", ", facility.Dns?.Servers ?? []);
        _check = facility.Check ?? "";
        _timeout = facility.Timeout.ToString(CultureInfo.InvariantCulture);
        _notes = facility.Notes ?? "";
        _links = string.Join(Environment.NewLine, facility.Links.Select(l => l.Name == l.Url ? l.Url : $"{l.Name} = {l.Url}"));
        Fields = [.. kind.Fields.Select(f => new SettingRow(this, f))];
    }

    /// <summary>The id follows the customer and name until someone types one.</summary>
    public bool AutoId { get; set; }

    public string Id
    {
        get => _id;
        set
        {
            AutoId = false;
            SetProperty(ref _id, value);
        }
    }

    public void SetAutoId(string id) => SetProperty(ref _id, id, nameof(Id));

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Title));
        }
    }

    public string Customer
    {
        get => _customer;
        set
        {
            if (SetProperty(ref _customer, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string Title => _name.Trim().Length > 0 ? _name.Trim() : "New facility";

    public string Subtitle => string.Join("  ·  ", new[] { _customer.Trim(), _kind.Title }.Where(s => s.Length > 0));

    // ---------------------------------------------------------------- its VPN client

    /// <summary>A new kind keeps the settings it shares with the old one and drops the rest.</summary>
    public ConnectorKind Kind
    {
        get => _kind;
        set
        {
            if (value is null || !SetProperty(ref _kind, value)) return;
            if (!value.IsCustom)
                foreach (var key in _settings.Keys.Where(k => !value.Fields.Any(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase))).ToList())
                    _settings.Remove(key);
            Fields.Clear();
            foreach (var setting in value.Fields) Fields.Add(new SettingRow(this, setting));
            Raise(nameof(Subtitle), nameof(SettingsText), nameof(PasswordText), nameof(CanRemovePassword));
        }
    }

    public ObservableCollection<SettingRow> Fields { get; }

    public string GetSetting(string key) => _settings.GetValueOrDefault(key, "");

    public void SetSetting(string key, string value)
    {
        if (value.Length == 0) _settings.Remove(key);
        else _settings[key] = value;
        OnPropertyChanged("Settings");
    }

    /// <summary>A connector of this gateway's own: its settings as "key = value" lines.</summary>
    public string SettingsText
    {
        get => string.Join(Environment.NewLine, _settings.Select(s => $"{s.Key} = {s.Value}"));
        set
        {
            _settings.Clear();
            foreach (var line in value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                var equals = line.IndexOf('=');
                if (equals > 0) _settings[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- its password

    /// <summary>Typed here, and encrypted for this machine when saved.</summary>
    public string NewPassword
    {
        get => _newPassword;
        set
        {
            if (!SetProperty(ref _newPassword, value)) return;
            if (value.Length > 0) _removePassword = false;
            Raise(nameof(PasswordText), nameof(CanRemovePassword));
        }
    }

    public bool CanRemovePassword => _password is { Length: > 0 } && !_removePassword && _newPassword.Length == 0;

    public void RemovePassword()
    {
        _newPassword = "";
        _removePassword = true;
        Raise(nameof(NewPassword), nameof(PasswordText), nameof(CanRemovePassword));
    }

    public string PasswordText =>
        _newPassword.Length > 0 ? "Encrypted when you save: only this gateway can read it."
        : _removePassword ? "The saved password goes when you save."
        : _password is { Length: > 0 } ? "One is saved. Type a new one to replace it."
        : _kind.SharesPassword ? "None here: the one in Settings is used. One typed here is for this facility alone."
        : "Encrypted when you save: only this gateway can read it.";

    /// <summary>Saved: what the file now holds becomes the password, and nothing waits any more.</summary>
    public void Accept(string? password)
    {
        _password = password;
        _newPassword = "";
        _removePassword = false;
        Raise(nameof(NewPassword), nameof(PasswordText), nameof(CanRemovePassword));
    }

    // ---------------------------------------------------------------- its network

    public bool Layer2
    {
        get => _layer2;
        set => SetProperty(ref _layer2, value);
    }

    public string Routes
    {
        get => _routes;
        set => SetProperty(ref _routes, value);
    }

    public string DnsDomains
    {
        get => _dnsDomains;
        set => SetProperty(ref _dnsDomains, value);
    }

    public string DnsServers
    {
        get => _dnsServers;
        set => SetProperty(ref _dnsServers, value);
    }

    public string Check
    {
        get => _check;
        set => SetProperty(ref _check, value);
    }

    public string Timeout
    {
        get => _timeout;
        set => SetProperty(ref _timeout, value);
    }

    // ---------------------------------------------------------------- for whoever connects

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    /// <summary>"Name = address" lines.</summary>
    public string Links
    {
        get => _links;
        set => SetProperty(ref _links, value);
    }

    // ---------------------------------------------------------------- whether it will do

    /// <summary>What keeps the gateway from using it, worded to follow "This facility".</summary>
    public string? Problem
    {
        get => _problem;
        set
        {
            if (SetProperty(ref _problem, value)) Raise(nameof(ProblemText), nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    public string? ProblemText => _problem is null ? null : $"This facility {_problem}.";

    /// <summary>Another facility has the same id.</summary>
    public bool Duplicate
    {
        get => _duplicate;
        set => SetProperty(ref _duplicate, value);
    }

    public string? FindProblem()
    {
        if (_name.Trim().Length == 0) return "needs a name";
        if (_kind.Fields.FirstOrDefault(f => f.Required && GetSetting(f.Key).Trim().Length == 0) is { } missing)
            return $"needs its {missing.Label.ToLowerInvariant()}";
        if (!int.TryParse(_timeout.Trim(), CultureInfo.InvariantCulture, out var timeout) || timeout <= 0)
            return "has a timeout that is not a number of seconds";
        if (LinkLines().FirstOrDefault(l => !Uri.TryCreate(ParseLink(l).Url, UriKind.Absolute, out _)) is { } link)
            return $"has a link without an address such as https://… or rdp://…: {link}";
        return ToFacility(protect: false).Problem();
    }

    /// <param name="protect">Encrypt a newly typed password, to save it; otherwise it only stands for one.</param>
    public Facility ToFacility(bool protect)
    {
        var domains = Lines(_dnsDomains);
        var servers = Lines(_dnsServers);
        return new Facility
        {
            Id = _id.Trim(),
            Name = _name.Trim(),
            Customer = _customer.Trim(),
            Connector = _kind.Name,
            Settings = _settings.Where(s => s.Value.Trim().Length > 0).ToDictionary(s => s.Key, s => s.Value.Trim()),
            Password = _newPassword.Length > 0 ? protect ? Secret.Protect(_newPassword) : "(new)"
                : _removePassword ? null
                : _password,
            Routes = Lines(_routes),
            Layer2 = _layer2,
            Dns = domains.Count == 0 && servers.Count == 0 ? null : new DnsRule(domains, servers),
            Check = _check.Trim() is { Length: > 0 } check ? check : null,
            Timeout = int.TryParse(_timeout.Trim(), CultureInfo.InvariantCulture, out var timeout) && timeout > 0 ? timeout : Facility.DefaultTimeout,
            Notes = _notes.Trim() is { Length: > 0 } notes ? notes : null,
            Links = [.. LinkLines().Select(ParseLink)],
        };
    }

    /// <summary>One per line, or separated by commas or spaces.</summary>
    private static List<string> Lines(string text) =>
        [.. text.Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private IEnumerable<string> LinkLines() => _links.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static FacilityLink ParseLink(string line)
    {
        var equals = line.IndexOf('=');
        // An address alone names itself; one with '=' in it (a query) is not split there.
        return equals > 0 && !line[..equals].Contains("://")
            ? new FacilityLink(line[..equals].Trim(), line[(equals + 1)..].Trim())
            : new FacilityLink(line, line);
    }
}

/// <summary>What keeps a connector's settings: a facility, or Settings for all of the connector's facilities.</summary>
public interface ISettingsOwner
{
    string GetSetting(string key);
    void SetSetting(string key, string value);
}

/// <summary>One of a connector's settings in a form, kept by its owner.</summary>
public sealed class SettingRow(ISettingsOwner owner, ConnectorField setting) : ObservableObject
{
    public ISettingsOwner Owner => owner;
    public ConnectorField Field => setting;

    public bool IsText => setting.Kind is FieldKind.Text or FieldKind.File or FieldKind.Program;
    public bool IsFile => setting.Kind == FieldKind.File;

    /// <summary>Has a Browse button: a file to copy in, or a program to point at.</summary>
    public bool CanBrowse => setting.Kind is FieldKind.File or FieldKind.Program;
    public bool IsFlag => setting.Kind == FieldKind.Flag;
    public bool IsChoice => setting.Kind == FieldKind.Choice;

    public string Value
    {
        get => owner.GetSetting(setting.Key);
        set
        {
            owner.SetSetting(setting.Key, value);
            Raise(nameof(Value), nameof(IsChecked), nameof(Choice));
        }
    }

    public bool IsChecked
    {
        get => Value == "true";
        set => Value = value ? "true" : "";
    }

    /// <summary>The first choice is the connector's default, and stands for an empty setting.</summary>
    public string? Choice
    {
        get => Value.Length > 0 ? Value : setting.Choices?.FirstOrDefault();
        set => Value = value == setting.Choices?.FirstOrDefault() ? "" : value ?? "";
    }
}
