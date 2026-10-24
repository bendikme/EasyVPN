using System.IO;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace EasyVPN.GatewayApp;

/// <summary>
/// An OpenVPN profile as the facility editor takes it in: the networks it routes, if it names any
/// (most servers push theirs instead), and what OpenVPN 2.6 no longer takes as it is.
/// </summary>
public static partial class OpenVpnProfile
{
    /// <param name="Routes">The networks its route lines send through the VPN.</param>
    /// <param name="OldCertificates">A certificate signed with SHA1 or MD5, or an RSA key under 2048 bits: OpenVPN 2.6 refuses them by default.</param>
    /// <param name="Changes">What was rewritten, said for the person.</param>
    public sealed record Reading(List<string> Routes, bool OldCertificates, List<string> Changes);

    /// <summary>Reads the profile at <paramref name="path"/> - the gateway's own copy - and rewrites in it what OpenVPN 2.6 no longer takes.</summary>
    public static Reading Adapt(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var options = lines.Select(Words).Where(w => w.Length > 0).Select(w => w[0]).ToHashSet();
        var routes = new List<string>();
        var changes = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            switch (Words(lines[i]))
            {
                // route network [mask [gateway]]: net_gateway sends it around the VPN, not through it.
                case ["route", var network, .. var rest] when IPAddress.TryParse(network, out var address) && rest is not [_, "net_gateway", ..]:
                    var mask = rest is [var m, ..] && IPAddress.TryParse(m, out var parsed) ? parsed : IPAddress.Broadcast;
                    if (Network(address, mask) is { } route) routes.Add(route);
                    break;

                // Removed in OpenVPN 2.4. It matched the server certificate's common name.
                case ["tls-remote", var subject]:
                    var name = CommonName().Match(subject) is { Success: true } cn ? cn.Groups[1].Value.Trim() : subject;
                    lines[i] = $"verify-x509-name \"{name}\" name";
                    changes.Add($"tls-remote, which OpenVPN 2.4 and later do not take, became verify-x509-name for {name}");
                    break;

                // OpenVPN 2.6 ignores cipher, so a server that cannot negotiate one, or knows only
                // this one, would find none in common. Blowfish and its like need OpenSSL's legacy provider.
                case ["cipher", var cipher] when !options.Contains("data-ciphers") && !options.Contains("data-ciphers-fallback"):
                    List<string> added = [$"data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305:{cipher}", $"data-ciphers-fallback {cipher}"];
                    if (LegacyCipher().IsMatch(cipher) && !options.Contains("providers")) added.Add("providers legacy default");
                    lines.InsertRange(i + 1, added);
                    i += added.Count;
                    changes.Add($"{cipher}, which OpenVPN 2.6 no longer takes from cipher, was added as {string.Join(" and ", added.Select(a => a.Split(' ')[0]))}");
                    break;

                // OpenVPN 2.6 allows no compression unless told: take compressed packets from the
                // server, but send none, which is the safe half.
                case ["comp-lzo" or "compress", ..] when !options.Contains("allow-compression"):
                    lines.Insert(i + 1, "allow-compression asym");
                    options.Add("allow-compression");
                    i++;
                    changes.Add($"{Words(lines[i - 1])[0]}, which OpenVPN 2.6 refuses by default, got allow-compression asym: compressed packets from the server are taken, none are sent");
                    break;
            }
        }
        if (changes.Count > 0) File.WriteAllLines(path, lines);
        return new([.. routes.Distinct()], HasOldCertificates(string.Join('\n', lines)), changes);
    }

    private static string[] Words(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('#') || text.StartsWith(';')) return [];
        return [.. Word().Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)];
    }

    private static string? Network(IPAddress address, IPAddress mask)
    {
        var bits = BitConverter.ToUInt32(mask.GetAddressBytes());
        var prefix = BitOperations.PopCount(bits);
        var bytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++) bytes[i] &= maskBytes[i];
        return prefix >= 8 ? new IPNetwork(new IPAddress(bytes), prefix).ToString() : null;
    }

    private static bool HasOldCertificates(string profile)
    {
        foreach (Match pem in Certificate().Matches(profile))
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem.Value);
                var signature = certificate.SignatureAlgorithm.FriendlyName ?? "";
                using var rsa = certificate.GetRSAPublicKey();
                if (signature.StartsWith("sha1", StringComparison.OrdinalIgnoreCase)
                    || signature.StartsWith("md5", StringComparison.OrdinalIgnoreCase)
                    || rsa?.KeySize < 2048)
                    return true;
            }
            catch (CryptographicException)
            {
                // Not one .NET can read; OpenVPN will say what it makes of it.
            }
        }
        return false;
    }

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex Word();

    [GeneratedRegex("CN=([^,/]+)")]
    private static partial Regex CommonName();

    [GeneratedRegex("^(BF|CAST5|DES|DESX|RC2|SEED|IDEA)-", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyCipher();

    [GeneratedRegex("-----BEGIN CERTIFICATE-----.+?-----END CERTIFICATE-----", RegexOptions.Singleline)]
    private static partial Regex Certificate();
}
