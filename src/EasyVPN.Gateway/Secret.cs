using System.Security.Cryptography;
using System.Text;

namespace EasyVPN.Gateway;

/// <summary>
/// VPN passwords in facilities.json, encrypted with DPAPI for this machine: only this computer can
/// decrypt them, and only SYSTEM and administrators can read the file. <c>EasyVPN.Gateway.exe
/// protect</c> makes the encrypted form.
/// </summary>
public static class Secret
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "EasyVPN.Gateway"u8.ToArray();

    public static string Protect(string value) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    /// <summary>A password written in clear is taken as it is; that is the administrator's call.</summary>
    public static string Reveal(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine))
            : value;
}
