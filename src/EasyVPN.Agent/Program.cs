using System.Text.Json.Serialization;
using EasyVPN;
using EasyVPN.Agent;

// The agent runs as a Windows service (SYSTEM, which the adapter, routes and DNS rules need) and
// is driven by the EasyVPN app over a local API. Started from a console it runs there instead.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "EasyVPN.Agent");
builder.WebHost.UseUrls(Defaults.AgentUrl);
// Only this machine talks to the agent. Refusing other Host names keeps out a web page that
// rebinds its own name to 127.0.0.1.
builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(services =>
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("EasyVPN.Agent");
    return new RecentLog(line => logger.LogInformation("{Line}", line));
});
builder.Services.AddSingleton<Tunnel>();
builder.Services.AddSingleton<Session>();
builder.Services.AddHostedService(services => services.GetRequiredService<Session>());

var app = builder.Build();

// A browser cannot add a custom header to a cross-site request without asking first, so a web
// page cannot make the agent connect or disconnect.
app.Use((context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) || context.Request.Headers.ContainsKey(Defaults.AgentHeader)) return next(context);
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    return Task.CompletedTask;
});

var api = app.MapGroup("/api");
api.MapGet("/status", (Session session) => session.Status);
api.MapGet("/settings", (Session session) => session.Settings);
api.MapPut("/settings", (AgentSettingsUpdate update, Session session) => session.UpdateSettings(update));
api.MapGet("/facilities", (Session session, CancellationToken ct) => Gateway(session, async () => Results.Ok(await session.FacilitiesAsync(ct))));
api.MapPost("/connect/{id}", (string id, Session session, CancellationToken ct) => Gateway(session, async () =>
{
    await session.ConnectAsync(id, ct);
    return Results.NoContent();
}));
api.MapPost("/disconnect", (Session session, CancellationToken ct) => Gateway(session, async () =>
{
    await session.DisconnectAsync(ct);
    return Results.NoContent();
}));

app.Run();

// A call that goes on to the gateway: when the gateway is the problem, say what it is.
static async Task<IResult> Gateway(Session session, Func<Task<IResult>> call)
{
    try
    {
        return await call();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        var detail = ex is InvalidOperationException ? ex.Message : session.Explain(ex);
        return Results.Problem(detail, statusCode: StatusCodes.Status502BadGateway);
    }
}
