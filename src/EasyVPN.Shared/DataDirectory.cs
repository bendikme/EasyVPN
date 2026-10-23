using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace EasyVPN;

/// <summary>
/// Where a service keeps its state: <c>%ProgramData%\EasyVPN\&lt;name&gt;</c>. It holds access keys,
/// a private key and VPN secrets, so a new folder is readable by SYSTEM and Administrators only.
/// <c>EASYVPN_DATA</c> moves the root, for running a service from a development build.
/// </summary>
public static class DataDirectory
{
    public static string Get(string name)
    {
        var custom = Environment.GetEnvironmentVariable("EASYVPN_DATA");
        var root = custom ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyVPN");
        var path = Path.Combine(root, name);
        if (Directory.Exists(path)) return path;

        if (custom is not null)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        Directory.CreateDirectory(root);
        new DirectoryInfo(path).Create(security);
        return path;
    }

    public static T? ReadJson<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Defaults.Json) : default;

    /// <summary>Writes beside the file and swaps it in, so a crash never leaves half a file.</summary>
    public static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Defaults.Json));
        File.Move(temp, path, overwrite: true);
    }
}
