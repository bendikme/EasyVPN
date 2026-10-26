using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using EasyVPN;
using EasyVPN.Agent;
using EasyVPN.Gateway;

internal static class ServiceChecks
{
    // Start the real gateway entry point in a disposable directory. There is no gost.exe,
    // and sharing.ps1 and the connector are stand-ins: no adapter, route, DNS or ICS changes.
    public static async Task RunAsync(string root)
    {
        EnsurePortAvailable(Defaults.GatewayLocalPort);
        var apiPort = FreePort();
        var folder = Directory.CreateDirectory(Path.Combine(root, "service")).FullName;
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (Path.GetExtension(file) is ".dll" or ".json")
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(folder, "sharing.ps1"), "param([string]$Action)\nWrite-Output 'stand-in sharing'\nexit 0");
        var connectors = Directory.CreateDirectory(Path.Combine(GatewaySettings.Folder, "connectors")).FullName;
        File.WriteAllText(Path.Combine(connectors, "test.ps1"), """
            param([string]$Action)
            if ($env:EASYVPN_FACILITY -eq 'fails' -and $Action -eq 'Connect') { Write-Output 'expected connector failure'; exit 1 }
            Write-Output "$Action finished"
            exit 0
            """);
        DataDirectory.WriteJson(Path.Combine(GatewaySettings.Folder, "gateway.json"), new GatewaySettings
        {
            ListenAddress = "127.0.0.1", ApiPort = apiPort, RelayPort = FreePort(), AccessKey = "test-access-key",
        });
        DataDirectory.WriteJson(Path.Combine(GatewaySettings.Folder, "facilities.json"), new FacilityFile(
        [
            new Facility { Id = "first", Name = "First", Connector = "test", Routes = ["203.0.113.0/24"] },
            new Facility { Id = "second", Name = "Second", Connector = "test", Routes = ["203.0.113.0/24"] },
            new Facility { Id = "fails", Name = "Fails", Connector = "test", Routes = ["203.0.113.0/24"] },
        ]));

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(folder, "EasyVPN.Gateway.dll"));
        start.Environment["EASYVPN_DATA"] = root;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        using var process = new Process { StartInfo = start };
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Defaults.GatewayLocalPort}"), Timeout = TimeSpan.FromSeconds(2) };
            await WaitFor(async () =>
            {
                if (process.HasExited) throw new Exception("Gateway exited: " + string.Join('\n', output.TakeLast(10)));
                try { return (await local.GetAsync("/local/overview")).IsSuccessStatusCode; }
                catch (HttpRequestException) { return false; }
            });
            await Expect(local, HttpMethod.Get, "/api/status", HttpStatusCode.Forbidden);
            await Expect(local, HttpMethod.Post, "/local/disconnect", HttpStatusCode.Forbidden);
            using (var rebound = new HttpRequestMessage(HttpMethod.Get, "/local/overview"))
            {
                rebound.Headers.Host = "untrusted.example";
                using var response = await local.SendAsync(rebound);
                Require(response.StatusCode == HttpStatusCode.Forbidden, "Gateway accepted a rebound Host header");
            }
            local.DefaultRequestHeaders.Add(Defaults.AgentHeader, "1");
            await Expect(local, HttpMethod.Post, "/local/connect/missing", HttpStatusCode.NotFound);

            // Only the raw guard checks bypass certificate validation. GatewayClient below pins it.
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var remote = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(5) };
            await Expect(remote, HttpMethod.Get, "/api/status", HttpStatusCode.Unauthorized);
            remote.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-key");
            await Expect(remote, HttpMethod.Get, "/api/status", HttpStatusCode.Unauthorized);
            remote.DefaultRequestHeaders.Authorization = new("Bearer", "test-access-key");
            await Expect(remote, HttpMethod.Get, "/api/status", HttpStatusCode.OK);
            await Expect(remote, HttpMethod.Get, "/local/overview", HttpStatusCode.Unauthorized);
            await Expect(remote, HttpMethod.Post, "/api/connect/missing", HttpStatusCode.NotFound);
            await Expect(remote, HttpMethod.Get, "/api/layer2", HttpStatusCode.BadRequest);

            File.Delete(AgentSettings.CertificateFile);
            var settings = new AgentSettings { Gateway = $"127.0.0.1:{apiPort}", AccessKey = "test-access-key" };
            using var client = new GatewayClient(settings, thumbprint => settings.Thumbprint = thumbprint);
            Require((await client.FacilitiesAsync(CancellationToken.None)).Length == 3, "Client could not read the catalog");
            Require(settings.Thumbprint is { Length: 64 } && File.Exists(AgentSettings.CertificateFile), "Client did not pin the certificate");
            try { using var socket = await client.OpenLayer2Async(CancellationToken.None); throw new Exception("Gateway opened Layer 2 with no connected facility"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("port is closed")) { }
            var lease = await client.TunnelAsync(CancellationToken.None);
            Require(lease == await client.TunnelAsync(CancellationToken.None), "Client tunnel address changed");

            await client.ConnectAsync("first", CancellationToken.None);
            await WaitForState(client, ConnectionState.Connected, "first");
            await client.ConnectAsync("first", CancellationToken.None);
            await WaitForState(client, ConnectionState.Connected, "first");
            await client.ConnectAsync("second", CancellationToken.None);
            await WaitForState(client, ConnectionState.Connected, "second");
            await client.DisconnectAsync(CancellationToken.None);
            await WaitForState(client, ConnectionState.Disconnected, null);
            await client.ConnectAsync("fails", CancellationToken.None);
            await WaitForState(client, ConnectionState.Failed, "fails");
            await client.DisconnectAsync(CancellationToken.None);
            await WaitForState(client, ConnectionState.Disconnected, null);

            settings = new AgentSettings { Gateway = $"127.0.0.1:{apiPort}", AccessKey = "test-access-key", Thumbprint = new string('0', 64) };
            using var mismatched = new GatewayClient(settings, _ => throw new Exception("Unexpected certificate repin"));
            try { await mismatched.StatusAsync(CancellationToken.None); throw new Exception("Client accepted a mismatched certificate"); }
            catch (HttpRequestException) { }
            Console.WriteLine("  API authentication, Host/header guards, pinning, leases, connect/switch/disconnect and connector failures passed");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static async Task WaitForState(GatewayClient client, ConnectionState state, string? facility) => await WaitFor(async () =>
    {
        var status = await client.StatusAsync(CancellationToken.None);
        return status.State == state && status.FacilityId == facility;
    });

    private static async Task WaitFor(Func<Task<bool>> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Gateway did not reach the expected state within 15 seconds");
            await Task.Delay(100);
        }
    }

    private static async Task Expect(HttpClient client, HttpMethod method, string path, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(method, path);
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == status, $"{method} {path}: expected {(int)status}, got {(int)response.StatusCode}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void EnsurePortAvailable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException) { throw new Exception($"Stop the gateway before using --services: local port {port} is in use"); }
        finally { listener.Stop(); }
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
