// A test tool that plays an extension. It follows the extension contract (Sleipnir's
// docs/superpowers/plans/2026-11-15-two-modes.md) and shares no code with Bifröst.
//
//   FAKE_MODE=echo (default)  answer every request with what arrived, as JSON
//   FAKE_MODE=crash           exit 3 at once
//   FAKE_MODE=hang            never open the socket
//   FAKE_MODE=exit-after=<s>  echo, then exit 0 after <s> seconds
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
