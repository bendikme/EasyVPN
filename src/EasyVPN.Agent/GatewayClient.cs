using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyVPN.Agent;

/// <summary>
/// The gateway's API, over HTTPS. The gateway's certificate is self-signed, so it is pinned
/// instead: the first one seen is remembered (<paramref name="pin"/>), and from then on that
/// certificate and no other is accepted.
/// </summary>
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AgentSettings _settings;
    private readonly Action<string> _pin;

    public GatewayClient(AgentSettings settings, Action<string> pin)
    {
        _settings = settings;
        _pin = pin;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = Validate },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{settings.Host}:{settings.ApiPort}/api/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", settings.AccessKey);
        _http.DefaultRequestHeaders.Add(Defaults.ClientHeader, Environment.MachineName);
    }

    public async Task<GatewayStatus> StatusAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<GatewayStatus>("status", Defaults.Json, ct) ?? throw new HttpRequestException("The gateway sent no status");

    public async Task<FacilityInfo[]> FacilitiesAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<FacilityInfo[]>("facilities", Defaults.Json, ct) ?? [];

    public async Task ConnectAsync(string id, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"connect/{Uri.EscapeDataString(id)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"The gateway has no facility '{id}'");
        response.EnsureSuccessStatusCode();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("disconnect", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>What went wrong talking to the gateway, said so someone can act on it.</summary>
    public static string Explain(Exception ex, AgentSettings settings) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "The gateway did not accept the access key. Check it in Settings.",
        HttpRequestException { InnerException: AuthenticationException } =>
            "The gateway's certificate is not the one this PC pinned. If the gateway was reinstalled, forget the old certificate in Settings.",
        HttpRequestException or TaskCanceledException =>
            $"The gateway at {settings.Gateway} does not answer.",
        _ => ex.Message,
    };

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var thumbprint = presented.GetCertHashString(HashAlgorithmName.SHA256);

        if (string.IsNullOrEmpty(_settings.Thumbprint))
            _pin(thumbprint);
        else if (!string.Equals(thumbprint, _settings.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(AgentSettings.CertificateFile))
            File.WriteAllText(AgentSettings.CertificateFile, presented.ExportCertificatePem());
        return true;
    }

    public void Dispose() => _http.Dispose();
}
