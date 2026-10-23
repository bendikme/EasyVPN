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
