using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Rta.App;
using Rta.App.Hub;
using Rta.App.Node;
using Rta.App.Security;
using Rta.Core.Calibration;

var builder = WebApplication.CreateBuilder(args);

// Mode: Node (capture only), Hub (dashboard/logging only) or Both (default: one PC does everything).
var role = AppMode.Parse(builder.Configuration["Mode"]);
// A Hub must be reachable from the LAN; a node-only PC stays local unless told otherwise.
var urls = builder.Configuration["Urls"] ?? (role == AppRole.Node ? "http://localhost:5180" : "http://0.0.0.0:5180");
builder.WebHost.UseUrls(urls);
var port = Uri.TryCreate(urls.Split(';')[0].Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u) ? u.Port : 5180;
var mode = new AppMode(role, port);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = CalibrationStore.Json.PropertyNamingPolicy;
    foreach (var c in CalibrationStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Services.AddSingleton(mode);
builder.Services.AddSingleton<NodePaths>();
builder.Services.AddSingleton<AdminAuth>();

var dataDir = new NodePaths(builder.Configuration).DataDir;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("RtaApp");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "rta_admin";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

if (mode.RunsNode)
{
    builder.Services.AddSingleton<NodeConfigStore>();
    builder.Services.AddSingleton(sp => new CalibrationStore(sp.GetRequiredService<NodePaths>().CalibrationDir));
    builder.Services.AddSingleton<MeteringService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MeteringService>());
    builder.Services.AddSingleton<NodeBuffer>();
    builder.Services.AddSingleton<HubClient>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HubClient>());
}

if (mode.RunsHub)
{
    builder.Services.AddSingleton<HubStore>();
    builder.Services.AddSingleton<HubSettingsStore>();
    builder.Services.AddSingleton<HubState>();
    builder.Services.AddSingleton<NodeIngest>();
    builder.Services.AddHostedService<HubBeaconService>();
    builder.Services.AddHostedService<ServiceScheduler>();
    builder.Services.AddHostedService<AlertEngine>();
    builder.Services.AddHostedService<RetentionService>();
}

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// Static files run before routing so /node/ and /assets are served directly. "/" is the Hub
// dashboard; on a node-only PC (no Hub API) it redirects to the node page.
app.Use(async (ctx, next) =>
{
    if (!mode.RunsHub && (ctx.Request.Path == "/" || ctx.Request.Path == "/index.html")) { ctx.Response.Redirect("/node/"); return; }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAdminGuard();

app.MapGet("/api/app", () => new { role = mode.Role, runsNode = mode.RunsNode, runsHub = mode.RunsHub, version = AppMode.Version });
app.MapAuthEndpoints();
if (mode.RunsNode) app.MapNodeEndpoints();
if (mode.RunsHub) app.MapHubEndpoints();

// Client-side routes of the dashboard (e.g. /history) fall back to its index.html.
if (mode.RunsHub)
    app.MapFallback(async (HttpContext ctx, IWebHostEnvironment env) =>
    {
        var index = env.WebRootFileProvider.GetFileInfo("index.html");
        if (ctx.Request.Path.StartsWithSegments("/api") || !index.Exists) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(index);
    });

app.Logger.LogInformation("RTA app {Version} running as {Role} on {Urls} (data: {DataDir})", AppMode.Version, mode.Role, urls, dataDir);
app.Run();
