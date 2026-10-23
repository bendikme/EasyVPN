using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's own settings, in <c>%ProgramData%\EasyVPN\Gateway\gateway.json</c>. The access
/// key is made on first start; clients need it for the API, and the relay takes it as its password.
/// </summary>
public sealed class GatewaySettings
{
    /// <summary>The address the API and the relay listen on; every address when not set.</summary>
    public string? ListenAddress { get; set; }

    public int ApiPort { get; set; } = Defaults.GatewayApiPort;
    public int RelayPort { get; set; } = Defaults.GatewayRelayPort;
    public string AccessKey { get; set; } = "";

    public static string Folder => DataDirectory.Get("Gateway");

    public static GatewaySettings LoadOrCreate()
    {
        var path = Path.Combine(Folder, "gateway.json");
        var settings = DataDirectory.ReadJson<GatewaySettings>(path) ?? new GatewaySettings();
        if (settings.AccessKey.Length == 0)
        {
            settings.AccessKey = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            DataDirectory.WriteJson(path, settings);
        }
        return settings;
    }

    public bool Authorizes(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), Encoding.UTF8.GetBytes(AccessKey));
    }
}
