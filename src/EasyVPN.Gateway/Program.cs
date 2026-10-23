using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using EasyVPN;
using EasyVPN.Gateway;

// EasyVPN.Gateway.exe           runs the gateway: as the Windows service, or in a console to try it out
// EasyVPN.Gateway.exe info      prints what a client needs to connect
// EasyVPN.Gateway.exe protect   encrypts a VPN password for facilities.json
switch (args)
{
    case ["info"]:
        PrintInfo();
        return 0;
    case ["protect"]:
        Console.WriteLine(Secret.Protect(ReadPassword()));
        return 0;
}

var settings = GatewaySettings.LoadOrCreate();
var certificate = GatewayCertificate.LoadOrCreate();
GostProcess.KillStrays();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "EasyVPN.Gateway");
builder.WebHost.ConfigureKestrel(k => k.Listen(
    settings.ListenAddress is { } address ? IPAddress.Parse(address) : IPAddress.Any,
    settings.ApiPort,
    listen => listen.UseHttps(certificate.Certificate)));
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
builder.Services.AddSingleton<Relay>();
builder.Services.AddSingleton<ConnectionController>();
builder.Services.AddHostedService(services => services.GetRequiredService<ConnectionController>());

var app = builder.Build();

// Nothing here is public: every call carries the access key.
app.Use((context, next) =>
{
    if (settings.Authorizes(context.Request)) return next(context);
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    return Task.CompletedTask;
});

var api = app.MapGroup("/api");
api.MapGet("/facilities", (FacilityCatalog catalog) => catalog.All().Select(f => f.ToInfo()));
api.MapGet("/status", (ConnectionController controller) => controller.Status);
api.MapPost("/connect/{id}", (string id, HttpRequest request, ConnectionController controller) =>
    controller.Connect(id, ClientOf(request)) ? Results.Ok(controller.Status) : Results.NotFound());
api.MapPost("/disconnect", (HttpRequest request, ConnectionController controller) =>
{
    controller.Disconnect(ClientOf(request));
    return controller.Status;
});

app.Run();
return 0;

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
        Clients also need TCP {settings.ApiPort} and {settings.RelayPort} open to this machine.
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
