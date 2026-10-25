using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
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
    private readonly SocketsHttpHandler _handler;
    private readonly AgentSettings _settings;
    private readonly Action<string> _pin;

    public GatewayClient(AgentSettings settings, Action<string> pin)
    {
        _settings = settings;
        _pin = pin;
        _handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = { RemoteCertificateValidationCallback = Validate },
        };
        _http = new HttpClient(_handler)
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

    /// <summary>This PC's address on the tunnel. Null from a gateway too old to give one.</summary>
    public async Task<TunnelLease?> TunnelAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("tunnel", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TunnelLease>(Defaults.Json, ct);
    }

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

    /// <summary>
    /// The gateway's Layer 2 port: a WebSocket on the same address, key and pinned certificate as
    /// the API, carrying Ethernet frames.
    /// </summary>
    public async Task<WebSocket> OpenLayer2Async(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_settings.AccessKey}");
        socket.Options.SetRequestHeader(Defaults.ClientHeader, Environment.MachineName);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            var address = new Uri($"wss://{_settings.Host}:{_settings.ApiPort}/api/layer2");
            await socket.ConnectAsync(address, new HttpMessageInvoker(_handler, disposeHandler: false), ct);
            return socket;
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new InvalidOperationException(status == HttpStatusCode.Conflict
                ? "The gateway's Layer 2 port is closed: the facility is not connected there."
                : "The gateway could not open its Layer 2 adapter. Its activity log says why.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
