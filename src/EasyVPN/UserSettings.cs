using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace EasyVPN;

/// <summary>This user's own preferences, in <c>%LOCALAPPDATA%\EasyVPN\app.json</c>. The gateway is the agent's business.</summary>
public sealed class UserSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyVPN", "app.json");

    public List<string> Favourites { get; set; } = [];

    /// <summary>The facilities this user connected lately, by id, the latest first.</summary>
    public List<string> Recent { get; set; } = [];

    /// <summary>Groups this user opened or closed, by path. One never touched is closed (<see cref="FacilityTree.OpenByDefault"/>).</summary>
    public Dictionary<string, bool> Groups { get; set; } = [];

    /// <summary>Windows notifications when the connection changes: connected, interrupted, failed, disconnected by someone else.</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>The shortcut that opens the tray menu from anywhere, as DynatecRDM's Ctrl+Alt+R does. Empty for none.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F";

    /// <summary>The window's activity panel open, rather than folded away.</summary>
    public bool ShowActivity { get; set; }

    private const int RecentKept = 8;

    public static UserSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Defaults.Json) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Defaults.Json));
    }

    public bool IsFavourite(string id) => Favourites.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetFavourite(string id, bool favourite)
    {
        Favourites.RemoveAll(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));
        if (favourite) Favourites.Add(id);
        Save();
    }

    public void Used(string id)
    {
        Recent.RemoveAll(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, id);
        if (Recent.Count > RecentKept) Recent.RemoveRange(RecentKept, Recent.Count - RecentKept);
        Save();
    }

    public bool IsOpen(string group, bool otherwise) => Groups.TryGetValue(group, out var open) ? open : otherwise;

    public void SetOpen(string group, bool open)
    {
        Groups[group] = open;
        Save();
    }

    /// <summary>Starts in the notification area when this user signs in.</summary>
    public static bool StartWithWindows
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("EasyVPN") is not null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("EasyVPN", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("EasyVPN", throwOnMissingValue: false);
        }
    }
}
