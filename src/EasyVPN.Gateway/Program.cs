using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using EasyVPN;
using EasyVPN.Gateway;
using Microsoft.Extensions.Hosting.WindowsServices;

// EasyVPN.Gateway.exe           runs the gateway: as the Windows service, or in a console to try it out
// EasyVPN.Gateway.exe info      prints what a client needs to connect
// EasyVPN.Gateway.exe protect   encrypts a VPN password for facilities.json
// EasyVPN.Gateway.exe desktop … runs the gateway app with those arguments in a signed-in desktop
//                               session, for a connector that drives a VPN client's window
switch (args)
{
    case ["desktop", .. var rest]:
        try
        {
            return DesktopSession.RunApp([.. rest], TimeSpan.FromMinutes(4));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"The EasyVPN Gateway app could not be started on the desktop: {ex.Message}");
            return 1;
        }
    case ["info"]:
        PrintInfo();
        return 0;
    case ["protect"]:
        Console.WriteLine(Secret.Protect(ReadPassword()));
        return 0;
}

var settings = GatewaySettings.LoadOrCreate();
var certificate = GatewayCertificate.LoadOrCreate();
LegacyGost.KillStrays();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "EasyVPN.Gateway");
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(
        settings.ListenAddress is { } address ? IPAddress.Parse(address) : IPAddress.Any,
        settings.ApiPort,
        listen => listen.UseHttps(certificate.Certificate));
    // For the EasyVPN Gateway app on this machine's desktop.
    k.Listen(IPAddress.Loopback, Defaults.GatewayLocalPort);
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(certificate);
builder.Services.AddSingleton(services =>
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("EasyVPN.Gateway");
    return new RecentLog(line => logger.LogInformation("{Line}", line));
});
builder.Services.AddSingleton<FacilityCatalog>();
builder.Services.AddSingleton<Connectors>();
builder.Services.AddSingleton<Hub>();
builder.Services.AddSingleton<Sharing>();
builder.Services.AddSingleton<TunnelAddresses>();
builder.Services.AddSingleton<Layer2Port>();
builder.Services.AddSingleton<GatewayChecks>();
builder.Services.AddSingleton<ConnectionController>();
builder.Services.AddHostedService(services => services.GetRequiredService<ConnectionController>());
// Only the installed service updates itself, and by itself only while no facility is connected.
builder.Services.AddSingleton(services => new Updater(
    "Gateway",
    GatewaySettings.Folder,
    () => services.GetRequiredService<ConnectionController>().Status.State is ConnectionState.Disconnected or ConnectionState.Failed,
    services.GetRequiredService<RecentLog>(),
    WindowsServiceHelpers.IsWindowsService()));

var app = builder.Build();

// Nothing here is public: every call from a workstation carries the access key. The app on this
// machine's desktop has its own port, on 127.0.0.1 only, for /local and nothing else. It refuses
// other Host names (a web page that rebinds its own name to 127.0.0.1) and, for changes, calls
// without the app's header (which a page in a browser cannot add without asking first).
app.Use((context, next) =>
{
    var request = context.Request;
    var local = context.Connection.LocalPort == Defaults.GatewayLocalPort;
    var allowed = local
        ? request.Path.StartsWithSegments("/local")
            && request.Host.Host is "127.0.0.1" or "localhost"
            && (HttpMethods.IsGet(request.Method) || request.Headers.ContainsKey(Defaults.AgentHeader))
        : !request.Path.StartsWithSegments("/local") && settings.Authorizes(request);
    if (allowed) return next(context);
    context.Response.StatusCode = local ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
    return Task.CompletedTask;
});

// The tunnel and Layer 2 links ride on WebSockets. Pings find a workstation that vanished, so a
// stale link cannot hold its address, or the adapter.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

var api = app.MapGroup("/api");
api.Map("/layer2", (HttpContext context, Layer2Port port) => port.RunAsync(context, ClientOf(context.Request)));
// The tunnel: the workstation's IP packets, at its address on the tunnel.
api.Map("/tunnel/link", (HttpContext context, Hub hub, TunnelAddresses addresses) => hub.RunLinkAsync(context, addresses.For(ClientOf(context.Request))));
api.MapGet("/facilities", (FacilityCatalog catalog) => catalog.All().Select(f => f.ToInfo()));
api.MapGet("/status", (ConnectionController controller) => controller.Status);
// The workstation's address on the tunnel: the same each time.
api.MapPost("/tunnel", (HttpRequest request, TunnelAddresses addresses) => addresses.For(ClientOf(request)));
api.MapPost("/connect/{id}", (string id, HttpRequest request, ConnectionController controller) =>
    controller.Connect(id, ClientOf(request)) ? Results.Ok(controller.Status) : Results.NotFound());
api.MapPost("/disconnect", (HttpRequest request, ConnectionController controller) =>
{
    controller.Disconnect(ClientOf(request));
    return controller.Status;
});

var desktop = $"{Environment.MachineName} (gateway desktop)";
var local = app.MapGroup("/local");
local.MapGet("/overview", (ConnectionController controller, FacilityCatalog catalog, Updater updater) => new GatewayOverview(
    controller.Status,
    Environment.MachineName,
    Addresses(),
    settings.ApiPort,
    certificate.Thumbprint,
    [.. catalog.All().Select(f => f.ToInfo())],
    catalog.Problems,
    catalog.FilePath,
    updater.Status));
local.MapPost("/update/check", (Updater updater, CancellationToken ct) => updater.CheckNowAsync(ct));
local.MapPost("/update/install", (Updater updater) => updater.InstallNow());
// What is known at once; ?refresh=true to check everything again, in the background.
local.MapGet("/checks", (GatewayChecks checks, bool? refresh) => checks.Results(refresh == true));
local.MapPost("/connect/{id}", (string id, ConnectionController controller) =>
    controller.Connect(id, desktop) ? Results.NoContent() : Results.NotFound());
local.MapPost("/disconnect", (ConnectionController controller) =>
{
    controller.Disconnect(desktop);
    return Results.NoContent();
});
// Adds the "EasyVPN L2" adapter: it takes SYSTEM or an administrator, which this service is.
local.MapPost("/layer2", async (RecentLog log, CancellationToken ct) =>
{
    try
    {
        await Layer2Setup.EnsureAdapterAsync(workstation: false, log.Add, ct);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Services.GetRequiredService<Updater>().Start(app.Lifetime.ApplicationStopping);
app.Run();
return 0;

// Where workstations can find this machine: the addresses of its networks with a way out.
static IReadOnlyList<string> Addresses() =>
[
    .. NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().GatewayAddresses.Count > 0)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()),
];

static string ClientOf(HttpRequest request) =>
    request.Headers[Defaults.ClientHeader].FirstOrDefault()
    ?? request.HttpContext.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";

static void PrintInfo()
{
    var settings = GatewaySettings.LoadOrCreate();
    var certificate = GatewayCertificate.LoadOrCreate();
    Console.WriteLine($"""
        For each client, in EasyVPN > Settings:
          Gateway      {Environment.MachineName}:{settings.ApiPort}   (or this machine's IP address)
          Access key   {settings.AccessKey}

        The client pins this certificate the first time it connects; check it shows the same:
          {certificate.Thumbprint}

        Facilities are defined in {Path.Combine(GatewaySettings.Folder, "facilities.json")}
        Clients need TCP {settings.ApiPort} open to this machine, for the API and the tunnel. They reach
        a connected facility through Windows' Internet Connection Sharing, which shares its VPN with them.
        The EasyVPN Gateway app, in the Start menu, shows all of this and how the gateway is doing.
        """);
}

static string ReadPassword()
{
    while (true)
    {
        var first = ReadHidden("Password: ");
        if (first == ReadHidden("Again:    ")) return first;
        Console.WriteLine("They differ; once more.");
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
