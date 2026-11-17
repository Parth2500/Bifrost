// A test tool that plays an extension. It follows the extension contract (Sleipnir's
// docs/superpowers/plans/2026-11-15-two-modes.md) and shares no code with Bifröst.
//
//   FAKE_MODE=echo (default)  answer every request with what arrived, as JSON
//   FAKE_MODE=crash           exit 3 at once
//   FAKE_MODE=hang            never open the socket
//   FAKE_MODE=exit-after=<s>  echo, then exit 0 after <s> seconds
//   FAKE_MODE=page            also serve a self-testing web page at / (for trying a client's web view)
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

if (args is not ["extension"]) return 64;

var mode = Environment.GetEnvironmentVariable("FAKE_MODE") ?? "echo";
if (mode == "crash") return 3;
if (mode == "hang")
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}

var socket = Environment.GetEnvironmentVariable("BIFROST_SOCKET") ?? throw new InvalidOperationException("BIFROST_SOCKET is not set");
var dataDir = Environment.GetEnvironmentVariable("BIFROST_DATA_DIR") ?? ".";
if (File.Exists(socket)) File.Delete(socket);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket));
var app = builder.Build();
app.UseWebSockets();

if (mode == "page")
{
    app.MapGet("/", () => Results.Content(Page.Html, "text/html; charset=utf-8"));
    app.MapGet("/index.html", () => Results.Content(Page.Html, "text/html; charset=utf-8"));
    app.MapGet("/app.js", () => Results.Content(Page.Script, "text/javascript; charset=utf-8"));
    app.MapGet("/style.css", () => Results.Content(Page.Style, "text/css; charset=utf-8"));
    app.MapGet("/pixel.svg", () => Results.Content("<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><rect width='8' height='8' fill='green'/></svg>", "image/svg+xml"));
    app.MapGet("/data.json", () => Results.Json(new { hello = "from the extension" }));
    // The page reports with a GET: a web view that hands every request to the app cannot send a body.
    app.MapGet("/report", async (string r) => { await File.WriteAllTextAsync(Path.Combine(dataDir, "page-report.json"), r); return Results.NoContent(); });
    // Live updates without a WebSocket: a stream of events over an ordinary GET.
    app.MapGet("/events", async (HttpContext context) =>
    {
        context.Response.ContentType = "text/event-stream";
        for (var i = 1; i <= 3; i++)
        {
            await context.Response.WriteAsync($"data: {i}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(300, context.RequestAborted);
        }
    });
    app.Map("/ws", async (HttpContext context) =>
    {
        if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest();
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[1024];
        var received = await ws.ReceiveAsync(buffer, context.RequestAborted);
        await ws.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, true, context.RequestAborted);
        await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        return Results.Empty;
    });
}

app.MapGet("/big", async (HttpContext context, int mb) =>
{
    var chunk = new byte[1024 * 1024];
    context.Response.ContentType = "application/octet-stream";
    context.Response.ContentLength = (long)mb * chunk.Length;
    for (var i = 0; i < mb; i++) await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
});

app.MapGet("/slow", async (HttpContext context) =>
{
    try
    {
        while (true)
        {
            await context.Response.Body.WriteAsync(new byte[] { 1 }, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(1000, context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        await File.WriteAllTextAsync(Path.Combine(dataDir, "slow.log"), "cancelled");
    }
});

app.Map("/{**path}", async (HttpContext context) => // not MapFallback: that skips paths that look like file names
{
    using var body = new MemoryStream();
    await context.Request.Body.CopyToAsync(body);
    var env = Environment.GetEnvironmentVariables().Keys.Cast<string>()
        .Where(k => k.StartsWith("BIFROST_", StringComparison.Ordinal))
        .ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k));
    var echo = new
    {
        method = context.Request.Method,
        path = context.Features.Get<IHttpRequestFeature>()!.RawTarget.Split('?')[0],
        query = context.Request.QueryString.Value,
        headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
        env,
        bodyLength = body.Length,
    };
    return Results.Json(echo, new JsonSerializerOptions(JsonSerializerDefaults.Web));
});

await app.StartAsync();
File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);

if (mode.StartsWith("exit-after=", StringComparison.Ordinal))
{
    await Task.Delay(TimeSpan.FromSeconds(int.Parse(mode["exit-after=".Length..], System.Globalization.CultureInfo.InvariantCulture)));
    await app.StopAsync();
    return 0;
}

await app.WaitForShutdownAsync();
return 0;
