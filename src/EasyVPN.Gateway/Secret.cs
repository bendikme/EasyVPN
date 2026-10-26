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
    /// <exception cref="CryptographicException">It was encrypted on another computer, as when facilities.json is copied from another gateway.</exception>
    public static string Reveal(string value)
    {
        if (!IsProtected(value)) return value;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Windows says only "Key not valid for use in specified state".
            throw new CryptographicException("The VPN password was encrypted on another computer, so this gateway cannot read it. Type it again in this gateway's facilities.", ex);
        }
    }

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Whether this computer can decrypt it: false for one encrypted on another.</summary>
    public static bool Readable(string value)
    {
        try
        {
            Reveal(value);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
