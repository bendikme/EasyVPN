extern alias GatewayAppAssembly;

using System.Net;
using System.IO;
using System.Reflection;
using EasyVPN;
using EasyVPN.Agent;
using EasyVPN.Gateway;
using OpenVpnProfile = GatewayAppAssembly::EasyVPN.GatewayApp.OpenVpnProfile;
using FacilitiesViewModel = GatewayAppAssembly::EasyVPN.GatewayApp.FacilitiesViewModel;

// No VPN software, network changes, administrator rights or test packages are required.
var root = Path.Combine(Path.GetTempPath(), "EasyVPN.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var previousData = Environment.GetEnvironmentVariable("EASYVPN_DATA");
Environment.SetEnvironmentVariable("EASYVPN_DATA", root);
var failures = 0;
var count = 0;
try
{
    await Check("Static OpenVPN routes and net_gateway exclusions", () =>
    {
        var reading = Profile("route 10.20.7.3 255.255.255.0\nroute 10.0.0.0 255.0.0.0 net_gateway\nroute 172.16.1.1");
        Equal("10.20.7.0/24,172.16.1.1/32", string.Join(',', reading.Routes));
    });
    await Check("OpenVPN rewrites directives with inline comments", () =>
    {
        var reading = Profile("tls-remote \"legacy server\" # certificate name\ncipher AES-256-CBC ; old cipher");
        Equal(2, reading.Changes.Count);
    });
    await Check("OpenVPN adaptation is idempotent", () =>
    {
        var path = Path.Combine(root, "idempotent.ovpn");
        File.WriteAllText(path, "cipher BF-CBC\ncomp-lzo yes\ntls-remote \"CN=server,O=customer\"");
        Equal(3, OpenVpnProfile.Adapt(path).Changes.Count);
        Equal(0, OpenVpnProfile.Adapt(path).Changes.Count);
        Require(File.ReadAllText(path).Contains("providers legacy default"), "Legacy cipher provider missing");
    });
    await Check("IPv6 OpenVPN route input does not crash import", () =>
    {
        Equal(0, Profile("route 2001:db8:: 255.255.255.0").Routes.Count);
    });
    await Check("Noncontiguous OpenVPN masks do not become different networks", () =>
    {
        Equal(0, Profile("route 10.20.30.0 255.0.255.0").Routes.Count);
    });
    await Check("OpenVPN inline content is preserved", () =>
    {
        var reading = Profile("<auth-user-pass>\ncipher secret\ncomp-lzo password\n</auth-user-pass>\nroute 10.0.0.0 255.255.255.0");
        Equal(0, reading.Changes.Count);
        Equal("10.0.0.0/24", string.Join(',', reading.Routes));
    });
    await Check("OpenVPN data stays intact when real directives are rewritten", () =>
    {
        var path = Path.Combine(root, "inline.ovpn");
        var data = "<auth-user-pass>\nallow-compression secret\ndata-ciphers password\n</auth-user-pass>";
        File.WriteAllText(path, data + "\ncipher AES-256-CBC\ncomp-lzo yes");
        Equal(2, OpenVpnProfile.Adapt(path).Changes.Count);
        Require(File.ReadAllText(path).Replace("\r\n", "\n").StartsWith(data), "Inline credentials changed");
    });
    await Check("Quoted and escaped OpenVPN certificate names survive rewriting", () =>
    {
        var path = Path.Combine(root, "quoted.ovpn");
        File.WriteAllText(path, "tls-remote 'a # name; with \"quotes\"' # comment");
        Equal(1, OpenVpnProfile.Adapt(path).Changes.Count);
        Require(File.ReadAllText(path).Contains("a # name; with \\\"quotes\\\""), "Certificate name changed");
    });
    await Check("OpenVPN handles literal apostrophes, backslashes and directive prefixes", () =>
    {
        var path = Path.Combine(root, "literal.ovpn");
        File.WriteAllText(path, "--tls-remote customer's-vpn\ntls-remote 'customer\\server'\n<ca>\nignored\n</ca> # comment\n--route 10.8.0.0 255.255.255.0");
        var reading = OpenVpnProfile.Adapt(path);
        Equal(2, reading.Changes.Count);
        Equal("10.8.0.0/24", string.Join(',', reading.Routes));
        Require(File.ReadAllText(path).Contains("customer\\\\server"), "Literal backslash lost during rewrite");
    });
    await Check("OpenVPN rejects invalid masks and accepts default masks", () =>
    {
        var reading = Profile("route 10.0.0.1 invalid\nroute 10.0.0.2 default\nroute 10.0.0.3 \"\"\nroute 10.0.0.4 ::ffff:ffff");
        Equal("10.0.0.2/32,10.0.0.3/32", string.Join(',', reading.Routes));
    });
    await Check("Route overlap checks accept local host addresses", () =>
    {
        var tunnel = new Tunnel(new RecentLog());
        var routes = (List<string>)Invoke(tunnel, "Routes", FacilityInfo(), "127.0.0.1")!;
        Equal("10.222.0.0/24", string.Join(',', routes));
    });
    await Check("Routes containing the gateway are excluded", () =>
    {
        var tunnel = new Tunnel(new RecentLog());
        var routes = (List<string>)Invoke(tunnel, "Routes", FacilityInfo() with { Routes = ["127.0.0.0/8"] }, "127.0.0.1")!;
        Equal(0, routes.Count);
    });
    await Check("Facility ids cannot resolve to parent directories", () =>
    {
        foreach (var id in new[] { ".", "..", "...", "customer.", "customer\n", "NUL", "con.txt", "COM1" })
            Require(new Facility { Id = id }.Problem() is not null, $"Unsafe file name '{id}' accepted as facility id");
        Require(new Facility { Id = "customer-1" }.Problem() is null, "Valid facility id rejected");
    });
    await Check("Facilities with explicit null fields are reported", () =>
    {
        var facility = System.Text.Json.JsonSerializer.Deserialize<Facility>("{\"id\":\"null-routes\",\"routes\":null}", Defaults.Json)!;
        Require(facility.Problem() is not null, "Null routes accepted");
    });
    await Check("Catalog keeps valid facilities beside an invalid entry", () =>
    {
        var path = Path.Combine(GatewaySettings.Folder, "facilities.json");
        File.WriteAllText(path, "{\"facilities\":[null,{\"id\":\"valid\",\"name\":\"Valid\"}]}");
        var catalog = new FacilityCatalog(new RecentLog());
        Equal(1, catalog.All().Count);
        Equal(1, catalog.Problems.Count);
    });
    await Check("Malformed catalog keeps its last good facilities", () =>
    {
        var path = Path.Combine(GatewaySettings.Folder, "facilities.json");
        File.WriteAllText(path, "{\"facilities\":[{\"id\":\"valid\"}]}");
        var catalog = new FacilityCatalog(new RecentLog());
        Equal(1, catalog.All().Count);
        File.WriteAllText(path, "{");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Equal(1, catalog.All().Count);
        Equal(1, catalog.Problems.Count);
    });
    await CheckAsync("Editor reports malformed facility fields without overwriting", () => OnSta(() =>
    {
        var path = Path.Combine(GatewaySettings.Folder, "facilities.json");
        foreach (var json in new[] { "{\"facilities\":[null]}", "{\"facilities\":[{\"id\":\"test\",\"routes\":null}]}" })
        {
            File.WriteAllText(path, json);
            var editor = new FacilitiesViewModel(null, _ => true);
            Require(editor.Error is not null, "Malformed file was not reported");
            Require(!editor.Save(), "Malformed file could be overwritten");
            Equal(json, File.ReadAllText(path));
        }
    }));
    await CheckAsync("Editor saves valid facilities without losing their settings", () => OnSta(() =>
    {
        var path = Path.Combine(GatewaySettings.Folder, "facilities.json");
        DataDirectory.WriteJson(path, new FacilityFile([new Facility { Id = "editor", Name = "Editor", Routes = ["10.0.0.0/24"], Notes = "Keep me", Settings = new() { ["custom"] = "value" } }]));
        var editor = new FacilitiesViewModel("editor", _ => true);
        Require(!editor.IsDirty, "Unchanged editor is dirty");
        Require(editor.Save(), editor.Error ?? "Save failed");
        var saved = DataDirectory.ReadJson<FacilityFile>(path)!.Facilities.Single();
        Equal("Keep me", saved.Notes);
        Equal("value", saved.Settings["custom"]);
    }));
    await Check("A password encrypted on another computer says so", () =>
    {
        Equal("secret", Secret.Reveal(Secret.Protect("secret")));
        var foreign = "dpapi:" + Convert.ToBase64String(new byte[64]);
        Require(!Secret.Readable(foreign), "Foreign password readable");
        try
        {
            Secret.Reveal(foreign);
            Require(false, "Foreign password revealed");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Require(ex.Message.Contains("another computer"), ex.Message);
        }
    });
    await CheckAsync("Editor shows a password encrypted on another computer as a problem", () => OnSta(() =>
    {
        var path = Path.Combine(GatewaySettings.Folder, "facilities.json");
        DataDirectory.WriteJson(path, new FacilityFile([new Facility { Id = "copied", Name = "Copied", Routes = ["10.0.0.0/24"], Password = "dpapi:" + Convert.ToBase64String(new byte[64]) }]));
        var draft = new FacilitiesViewModel("copied", _ => true).Facilities.Single();
        Require(draft.Problem?.Contains("another computer") == true, draft.Problem ?? "No problem shown");
        draft.NewPassword = "typed again";
        Equal(null, draft.Problem);
    }));
    await CheckAsync("A password from another computer stops Connect but not Disconnect", async () =>
    {
        var folder = Directory.CreateDirectory(Path.Combine(GatewaySettings.Folder, "connectors")).FullName;
        File.WriteAllText(Path.Combine(folder, "copiedtest.ps1"), "param([string]$Action)\nexit 0");
        var facility = new Facility { Id = "copied", Name = "Copied", Connector = "copiedtest", Password = "dpapi:" + Convert.ToBase64String(new byte[64]) };
        var connectors = new Connectors(new RecentLog());
        Equal(0, await connectors.RunAsync(facility, "Disconnect", CancellationToken.None));
        try
        {
            await connectors.RunAsync(facility, "Connect", CancellationToken.None);
            Require(false, "Connected with a password from another computer");
        }
        catch (InvalidOperationException ex)
        {
            Require(ex.Message.Contains("another machine"), ex.Message);
        }
    });
    await Check("VPN route discovery filters uplinks and broadcasts", () =>
    {
        var before = new[] { Route("0.0.0.0", 0, 1) };
        var after = before.Concat(new[] { Route("10.22.0.0", 24, 5), Route("10.22.0.255", 32, 5), Route("224.0.0.0", 4, 5), Route("203.0.113.1", 32, 1), Route("198.18.0.0", 24, 6) }).ToArray();
        Equal("10.22.0.0/24", string.Join(',', VpnRoutes.Added(before, after)));
    });
    await CheckAsync("Disconnect releases the local link while gateway is offline", async () =>
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        new AgentSettings { Gateway = $"127.0.0.1:{port}", AccessKey = "test" }.Save();
        var log = new RecentLog();
        var session = new Session(new Tunnel(log), new Layer2Link(log), log);
        Field(session, "_carried").SetValue(session, FacilityInfo() with { Layer2 = true });
        // Disconnect has already cleared _wanted, but the gateway request failed.
        await (Task)Invoke(session, "PollAsync", CancellationToken.None)!;
        Require(Field(session, "_carried").GetValue(session) is null, "Local link stays carried while gateway is offline");
    });
    await CheckAsync("PowerShell helper captures output and exit codes", async () =>
    {
        var path = Path.Combine(root, "output.ps1");
        File.WriteAllText(path, "Write-Output $env:EASYVPN_TEST_VALUE\nWrite-Output 'finished'\nexit 7");
        var lines = new List<string>();
        var code = await PowerShell.RunFileAsync(path, [], new Dictionary<string, string> { ["EASYVPN_TEST_VALUE"] = "test value" }, TimeSpan.FromSeconds(10), lines.Add, CancellationToken.None);
        Equal(7, code);
        Equal("test value,finished", string.Join(',', lines));
    });
    await CheckAsync("PowerShell helper stops timed-out scripts", async () =>
    {
        var path = Path.Combine(root, "timeout.ps1");
        File.WriteAllText(path, "Start-Sleep -Seconds 30");
        var lines = new List<string>();
        var code = await PowerShell.RunFileAsync(path, [], new Dictionary<string, string>(), TimeSpan.FromMilliseconds(300), lines.Add, CancellationToken.None);
        Equal(-1, code);
        Require(lines.Any(l => l.Contains("was stopped")), "Timeout was not reported");
    });
    if (args.Contains("--services"))
        await CheckAsync("Gateway service API, guards and connection lifecycle", () => ServiceChecks.RunAsync(root));
}
finally
{
    Environment.SetEnvironmentVariable("EASYVPN_DATA", previousData);
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{count - failures}/{count} checks passed");
return failures == 0 ? 0 : 1;

async Task Check(string name, Action test) => await CheckAsync(name, () => { test(); return Task.CompletedTask; });
async Task CheckAsync(string name, Func<Task> test)
{
    count++;
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}"); }
}
OpenVpnProfile.Reading Profile(string content)
{
    var path = Path.Combine(root, "profile.ovpn");
    File.WriteAllText(path, content);
    return OpenVpnProfile.Adapt(path);
}
static FacilityInfo FacilityInfo() => new("test", "Test", "", "manual", ["10.222.0.0/24"], null, null, null, []);
static VpnRoutes.Route Route(string address, int prefix, int nic) => new(IPAddress.Parse(address), prefix, IPAddress.Any, nic);
static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
static object? Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'");
static async Task OnSta(Action action)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try { action(); completion.SetResult(); }
        catch (Exception ex) { completion.SetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    await completion.Task;
}
