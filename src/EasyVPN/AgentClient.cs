using System.Net.Http;
using System.Net.Http.Json;

namespace EasyVPN;

/// <summary>The agent service on this PC: everything the app knows and does goes through it.</summary>
public sealed class AgentClient
{
    private sealed record Problem(string? Title, string? Detail);

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri($"{Defaults.AgentUrl}/api/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    public AgentClient() => _http.DefaultRequestHeaders.Add(Defaults.AgentHeader, "1");

    public Task<AgentStatus> StatusAsync() => GetAsync<AgentStatus>("status");

    public Task<FacilityInfo[]> FacilitiesAsync() => GetAsync<FacilityInfo[]>("facilities");

    public Task<AgentSettingsView> SettingsAsync() => GetAsync<AgentSettingsView>("settings");

    public async Task SaveSettingsAsync(AgentSettingsUpdate update)
    {
        using var _ = await SendAsync(HttpMethod.Put, "settings", JsonContent.Create(update, options: Defaults.Json));
    }

    public async Task<UpdateStatus> CheckUpdatesAsync()
    {
        using var response = await SendAsync(HttpMethod.Post, "update/check");
        return await response.Content.ReadFromJsonAsync<UpdateStatus>(Defaults.Json) ?? throw new AgentException("The agent sent an empty answer.");
    }

    public async Task InstallUpdateAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "update/install");
    }

    public async Task ConnectAsync(string id)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"connect/{Uri.EscapeDataString(id)}");
    }

    public async Task DisconnectAsync()
    {
        using var _ = await SendAsync(HttpMethod.Post, "disconnect");
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        return await response.Content.ReadFromJsonAsync<T>(Defaults.Json) ?? throw new AgentException("The agent sent an empty answer.");
    }

    /// <exception cref="AgentException">The agent is not running, or said why it could not do it.</exception>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(new HttpRequestMessage(method, path) { Content = content });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AgentException("The EasyVPN Agent service is not running on this PC.");
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
            throw new AgentException(problem?.Detail ?? problem?.Title ?? $"The agent answered {(int)response.StatusCode}.");
        }
    }
}

public sealed class AgentException(string message) : Exception(message);
