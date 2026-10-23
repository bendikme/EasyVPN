using System.Net.Http;
using System.Net.Http.Json;

namespace EasyVPN.GatewayApp;

/// <summary>The gateway service on this machine, through the port it keeps for its own desktop.</summary>
public sealed class LocalGateway
{
    private sealed record Problem(string? Title, string? Detail);

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Defaults.GatewayLocalPort}/local/"),
        // Checking every facility's VPN client takes a while.
        Timeout = TimeSpan.FromMinutes(2),
    };

    public LocalGateway() => _http.DefaultRequestHeaders.Add(Defaults.AgentHeader, "1");

    public Task<GatewayOverview> OverviewAsync() => GetAsync<GatewayOverview>("overview");

    public Task<CheckResult[]> ChecksAsync() => GetAsync<CheckResult[]>("checks");

    public async Task ConnectAsync(string id)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"connect/{Uri.EscapeDataString(id)}");
    }

    public async Task DisconnectAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "disconnect");
    }

    public async Task AddLayer2AdapterAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "layer2");
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        return await response.Content.ReadFromJsonAsync<T>(Defaults.Json) ?? throw new GatewayException("The gateway sent an empty answer.");
    }

    /// <exception cref="GatewayException">The service is not running, or said why it could not do it.</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(new HttpRequestMessage(method, path));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new GatewayException("The EasyVPN Gateway service is not running on this machine.", serviceDown: true);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            Problem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<Problem>(Defaults.Json);
            }
            catch (Exception)
            {
                // Not a problem document; the status code will have to do.
            }
            throw new GatewayException(problem?.Detail ?? problem?.Title ?? $"The gateway answered {(int)response.StatusCode}.");
        }
    }
}

public sealed class GatewayException(string message, bool serviceDown = false) : Exception(message)
{
    public bool ServiceDown { get; } = serviceDown;
}
