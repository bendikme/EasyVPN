using System.Net.Http;
using System.Net.Http.Json;

namespace EasyVPN.GatewayApp;

/// <summary>The gateway service on this machine, through the port it keeps for its own desktop.</summary>
public sealed class LocalGateway
{
    private sealed record Problem(string? Title, string? Detail);

    // An answer that takes this long has not got stuck: the service is busy, not gone.
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Defaults.GatewayLocalPort}/local/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public LocalGateway() => _http.DefaultRequestHeaders.Add(Defaults.AgentHeader, "1");

    public Task<GatewayOverview> OverviewAsync() => GetAsync<GatewayOverview>("overview");

    /// <summary>What the service knows of its checks, at once; with <paramref name="refresh"/>, it checks everything again, in the background.</summary>
    public Task<GatewayCheckRun> ChecksAsync(bool refresh) => GetAsync<GatewayCheckRun>(refresh ? "checks?refresh=true" : "checks");

    public async Task ConnectAsync(string id)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"connect/{Uri.EscapeDataString(id)}");
    }

    public async Task DisconnectAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "disconnect");
    }

    public async Task CheckUpdatesAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "update/check", TimeSpan.FromMinutes(1));
    }

    public async Task InstallUpdateAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "update/install");
    }

    public async Task AddLayer2AdapterAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "layer2", TimeSpan.FromMinutes(3));
    }

    private async Task<T> GetAsync<T>(string path, TimeSpan? limit = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, limit);
        return await response.Content.ReadFromJsonAsync<T>(Defaults.Json) ?? throw new GatewayException("The gateway sent an empty answer.");
    }

    /// <exception cref="GatewayException">The service is not running, took too long, or said why it could not do it.</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TimeSpan? limit = null)
    {
        var wait = limit ?? Quick;
        HttpResponseMessage response;
        using var timeout = new CancellationTokenSource(wait);
        try
        {
            response = await _http.SendAsync(new HttpRequestMessage(method, path), timeout.Token);
        }
        catch (HttpRequestException)
        {
            // Nothing listens: the service is not there.
            throw new GatewayException("The EasyVPN Gateway service is not running on this machine.", serviceDown: true);
        }
        catch (TaskCanceledException)
        {
            // It is there, and busy.
            throw new GatewayException($"The EasyVPN Gateway service took longer than {Say(wait)} to answer.");
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

    private static string Say(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} minutes" : $"{span.TotalSeconds:0} s";
}

public sealed class GatewayException(string message, bool serviceDown = false) : Exception(message)
{
    public bool ServiceDown { get; } = serviceDown;
}
