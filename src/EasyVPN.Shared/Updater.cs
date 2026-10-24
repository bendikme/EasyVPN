using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace EasyVPN;

/// <summary>
/// Keeps a service, and the app that comes with it, up to date from the project's GitHub releases.
///
/// It looks for a newer release a minute after the service starts, then every few hours, and when
/// asked. A newer release's MSI for this product is downloaded into the service's data folder -
/// SYSTEM and administrators only, so nobody can swap it - checked against the release's
/// SHA256SUMS.txt, and installed with msiexec, silently. The MSI stops this service, replaces it
/// and starts the new one. A few seconds before that the status says Installing, so the apps can
/// start a watcher that brings them back afterwards, and exit.
///
/// It installs by itself only while <c>idle</c> says nothing would be cut off; otherwise the app's
/// button does it. updates.json in the data folder can turn the automatic part off, or name
/// another repository: <c>{ "automatic": false, "repository": "owner/name" }</c>.
/// </summary>
public sealed class Updater(string product, string folder, Func<bool> idle, RecentLog log, bool enabled)
{
    private const string Repository = "bendikme/EasyVPN";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan PendingEvery = TimeSpan.FromMinutes(15);

    /// <summary>Long enough for every app, polling every second or two, to see Installing.</summary>
    private static readonly TimeSpan AppsNotice = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HttpClient Http = CreateHttp();

    private sealed record Options(bool Automatic = true, string Repository = Repository);
    private sealed record Release(string TagName, List<Asset> Assets);
    private sealed record Asset(string Name, string BrowserDownloadUrl);

    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _sync = new();
    private readonly string _current = CurrentVersion();
    private UpdateStatus? _status;
    private Release? _release;

    public UpdateStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status ??= enabled
                    ? new(_current, UpdateState.Unknown, Automatic: Settings().Automatic)
                    : new(_current, UpdateState.Unknown, Message: "A development build: only an installed EasyVPN updates itself.", Automatic: false);
            }
        }
    }

    private string Folder => Directory.CreateDirectory(Path.Combine(folder, "updates")).FullName;

    /// <summary>Starts looking, until <paramref name="stop"/>.</summary>
    public void Start(CancellationToken stop)
    {
        if (enabled) _ = RunAsync(stop);
    }

    public async Task<UpdateStatus> CheckNowAsync(CancellationToken ct)
    {
        if (!enabled) return Status;
        await _busy.WaitAsync(ct);
        try
        {
            await CheckAsync(ct);
        }
        finally
        {
            _busy.Release();
        }
        return Status;
    }

    /// <summary>Installs the newer release found, whatever is connected. It carries on after the call returns.</summary>
    public UpdateStatus InstallNow()
    {
        if (enabled && Status.State is UpdateState.Available or UpdateState.Failed && _release is not null)
            _ = Task.Run(async () =>
            {
                await _busy.WaitAsync();
                try
                {
                    await InstallAsync(CancellationToken.None);
                }
                finally
                {
                    _busy.Release();
                }
            });
        return Status;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(FirstCheck, stop);
            while (!stop.IsCancellationRequested)
            {
                await _busy.WaitAsync(stop);
                try
                {
                    await CheckAsync(stop);
                    if (Status.State == UpdateState.Available && Settings().Automatic && idle()) await InstallAsync(stop);
                }
                finally
                {
                    _busy.Release();
                }
                // A newer version not installed yet, because something was connected, is tried again soon.
                await Task.Delay(Status.State == UpdateState.Available ? PendingEvery : CheckEvery, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The service is stopping.
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var automatic = Settings().Automatic;
        Set(new(_current, UpdateState.Checking, Automatic: automatic));
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await Http.GetFromJsonAsync<Release>($"https://api.github.com/repos/{Settings().Repository}/releases/latest", GitHubJson, limit.Token)
                ?? throw new InvalidDataException("GitHub sent an empty answer.");
            var latest = release.TagName.TrimStart('v');
            if (!IsNewer(latest, _current))
            {
                _release = null;
                Set(new(_current, UpdateState.UpToDate, latest, Checked: DateTimeOffset.Now, Automatic: automatic));
                return;
            }
            if (Installer(release) is null)
            {
                Set(new(_current, UpdateState.UpToDate, latest, $"Release {latest} has no installer for this.", DateTimeOffset.Now, automatic));
                return;
            }
            var announce = _release?.TagName != release.TagName;
            _release = release;
            Set(new(_current, UpdateState.Available, latest, Checked: DateTimeOffset.Now, Automatic: automatic));
            if (announce) log.Add($"EasyVPN {latest} is available");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested)
        {
            Set(new(_current, UpdateState.Failed, Message: $"Could not look for updates: {ex.Message}", Checked: DateTimeOffset.Now, Automatic: automatic));
        }
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        if (_release is not { } release || Installer(release) is not { } asset) return;
        var latest = release.TagName.TrimStart('v');
        var automatic = Settings().Automatic;
        try
        {
            Set(new(_current, UpdateState.Downloading, latest, $"Downloading {asset.Name}", Automatic: automatic));
            log.Add($"Downloading EasyVPN {latest}");
            var msi = await DownloadAsync(release, asset, ct);

            Set(new(_current, UpdateState.Installing, latest, $"Installing EasyVPN {latest}: it restarts in a moment", Automatic: automatic));
            log.Add($"Installing EasyVPN {latest}; this service and the app restart");
            await Task.Delay(AppsNotice, ct);

            // msiexec carries on when the MSI stops this service: a service's processes are not stopped with it.
            var logFile = Path.Combine(Folder, $"install-{latest}.log");
            using var install = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            {
                ArgumentList = { "/i", msi, "/qn", "/norestart", "/l*v", logFile },
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("msiexec did not start.");
            await install.WaitForExitAsync(ct);

            // Still here: the installer gave up before it replaced this service.
            throw new InvalidOperationException($"The installer stopped with exit code {install.ExitCode}; {logFile} says why.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Set(new(_current, UpdateState.Failed, latest, $"Updating to {latest} failed: {ex.Message}", DateTimeOffset.Now, automatic));
            log.Add(Status.Message!);
        }
    }

    /// <summary>The MSI, checked against the release's checksums. Only one is kept.</summary>
    private async Task<string> DownloadAsync(Release release, Asset asset, CancellationToken ct)
    {
        var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt")
            ?? throw new InvalidDataException("The release has no SHA256SUMS.txt to check the installer against.");
        var expected = (await Http.GetStringAsync(sums.BrowserDownloadUrl, ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1] == asset.Name)?[0]
            ?? throw new InvalidDataException($"SHA256SUMS.txt does not list {asset.Name}.");

        foreach (var old in Directory.EnumerateFiles(Folder, "*.msi*")) File.Delete(old);
        var path = Path.Combine(Folder, asset.Name);
        var partial = path + ".part";
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(TimeSpan.FromMinutes(30));
            await using var download = await Http.GetStreamAsync(asset.BrowserDownloadUrl, limit.Token);
            await using var file = File.Create(partial);
            await download.CopyToAsync(file, limit.Token);
        }

        string actual;
        await using (var file = File.OpenRead(partial)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{asset.Name} does not match its checksum, so it was not installed.");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    private Asset? Installer(Release release) =>
        release.Assets.FirstOrDefault(a => a.Name.StartsWith($"EasyVPN-{product}-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase));

    private Options Settings()
    {
        try
        {
            return DataDirectory.ReadJson<Options>(Path.Combine(folder, "updates.json")) ?? new Options();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Options();
        }
    }

    private void Set(UpdateStatus status)
    {
        lock (_sync) _status = status;
    }

    private static bool IsNewer(string latest, string current) =>
        Version.TryParse(latest, out var a) && Version.TryParse(current, out var b) && a > b;

    /// <summary>This build's version, as release.ps1 stamped it (1.2.3, without the +commit).</summary>
    private static string CurrentVersion() =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API refuses requests without one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"EasyVPN/{CurrentVersion()}");
        return http;
    }
}
