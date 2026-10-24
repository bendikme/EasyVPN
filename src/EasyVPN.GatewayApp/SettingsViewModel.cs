using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using EasyVPN.Gateway;
using Microsoft.Win32;

namespace EasyVPN.GatewayApp;

/// <summary>
/// The gateway app's Settings: settings.json as a form. For each connector with something its
/// facilities can share - Tosi Client's program and password, for one - those settings and that
/// password, used by every facility of it that has none of its own. Like the facility editor, it
/// runs in a copy of the app started as administrator, and the gateway reads the file again as soon
/// as it is saved.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions FileJson = new(Defaults.Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _path;
    private readonly Func<string, bool> _ask;
    private SharedSettings _file = new();
    private DateTime _stamp;
    private string _saved = "";
    private bool _canEdit;
    private string? _status;
    private string? _error;

    /// <param name="ask">Asks the person a yes-or-no question.</param>
    public SettingsViewModel(Func<string, bool> ask)
    {
        _ask = ask;
        _path = Path.Combine(DataDirectory.Get("Gateway"), SharedSettings.FileName);
        SaveCommand = new RelayCommand(() => Save(), () => _canEdit);
        BrowseCommand = new RelayCommand(p => Browse((SettingRow)p!));
        Load();
    }

    public ObservableCollection<ConnectorSection> Sections { get; } = [];

    public RelayCommand SaveCommand { get; }
    public RelayCommand BrowseCommand { get; }

    public string FilePath => _path;

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Saved, so whatever shows a password box can empty it.</summary>
    public event Action? Saved;

    public bool IsDirty => _canEdit && Serialize(protect: false) != _saved;

    private void Load()
    {
        Sections.Clear();
        _canEdit = false;
        try
        {
            _stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            _file = DataDirectory.ReadJson<SharedSettings>(_path) ?? new SharedSettings();
            foreach (var kind in ConnectorKinds.Known.Where(k => k.HasShared))
            {
                var section = new ConnectorSection(kind, _file.For(kind.Name));
                section.PropertyChanged += (_, _) => Status = null;
                Sections.Add(section);
            }
            _saved = Serialize(protect: false);
            _canEdit = true;
        }
        catch (UnauthorizedAccessException)
        {
            Error = "It takes an administrator to change the settings.";
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Error = $"{SharedSettings.FileName} could not be read, so nothing is changed here: {ex.Message}";
        }
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <returns>False when it was not saved: the person said no, or it could not be written.</returns>
    public bool Save()
    {
        if (!_canEdit) return false;
        if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _stamp
            && !_ask($"{SharedSettings.FileName} has changed since it was opened here. Save over those changes?"))
            return false;
        try
        {
            var file = Build(protect: true);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, FileJson));
            File.Move(temp, _path, overwrite: true);
            foreach (var section in Sections) section.Accept(file.For(section.Kind.Name)?.Password);
            _file = file;
            _stamp = File.GetLastWriteTimeUtc(_path);
            _saved = Serialize(protect: false);
            Error = null;
            Status = $"Saved at {DateTime.Now:HH:mm}. The gateway uses it straight away.";
            Saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"{SharedSettings.FileName} could not be saved: {ex.Message}";
            return false;
        }
    }

    /// <summary>The file as read, with each section shown here put in (or taken out, when empty).</summary>
    private SharedSettings Build(bool protect)
    {
        var file = new SharedSettings
        {
            Connectors = _file.Connectors
                .Where(c => !Sections.Any(s => string.Equals(s.Kind.Name, c.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(c => c.Key, c => c.Value),
        };
        foreach (var section in Sections)
            if (section.ToDefaults(protect) is { } defaults)
                file.Connectors[section.Kind.Name] = defaults;
        return file;
    }

    private string Serialize(bool protect) => JsonSerializer.Serialize(Build(protect), FileJson);

    /// <summary>A program installed here, pointed at where it is.</summary>
    private static void Browse(SettingRow row)
    {
        var dialog = new OpenFileDialog { Filter = row.Field.Filter ?? "All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true) row.Value = dialog.FileName;
    }
}

/// <summary>One connector in Settings: what all of its facilities share.</summary>
public sealed class ConnectorSection : ObservableObject, ISettingsOwner
{
    private readonly Dictionary<string, string> _settings;
    private string? _password;
    private string _newPassword = "";
    private bool _removePassword;

    public ConnectorSection(ConnectorKind kind, ConnectorDefaults? defaults)
    {
        Kind = kind;
        _settings = new Dictionary<string, string>(defaults?.Settings ?? [], StringComparer.OrdinalIgnoreCase);
        _password = defaults?.Password;
        Fields = [.. kind.Fields.Where(f => f.Shared).Select(f => new SettingRow(this, f))];
    }

    public ConnectorKind Kind { get; }

    public string Heading => Kind.Title.ToUpperInvariant();

    public string Description => $"For every {Kind.Title} facility that has none of its own.";

    public IReadOnlyList<SettingRow> Fields { get; }

    public string GetSetting(string key) => _settings.GetValueOrDefault(key, "");

    public void SetSetting(string key, string value)
    {
        if (value.Trim().Length == 0) _settings.Remove(key);
        else _settings[key] = value.Trim();
        OnPropertyChanged("Settings");
    }

    // ---------------------------------------------------------------- its password

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
        : $"None yet. Every {Kind.Title} facility without a password of its own uses this one.";

    public void Accept(string? password)
    {
        _password = password;
        _newPassword = "";
        _removePassword = false;
        Raise(nameof(NewPassword), nameof(PasswordText), nameof(CanRemovePassword));
    }

    /// <summary>What the file keeps for this connector; null when nothing.</summary>
    /// <param name="protect">Encrypt a newly typed password, to save it; otherwise it only stands for one.</param>
    public ConnectorDefaults? ToDefaults(bool protect)
    {
        var password = _newPassword.Length > 0 ? protect ? Secret.Protect(_newPassword) : "(new)"
            : _removePassword ? null
            : _password;
        if (_settings.Count == 0 && password is null) return null;
        return new ConnectorDefaults { Settings = new Dictionary<string, string>(_settings), Password = password };
    }
}
