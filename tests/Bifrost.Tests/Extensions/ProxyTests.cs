using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Core.Models;
using Bifrost.Tests.Server;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Extensions;

public class ProxyTests
{
    private static async Task<HostFixture> HomeAsync(params (string Name, string Mode)[] extensions)
    {
        var map = extensions.Length == 0 ? new Dictionary<string, string> { ["sleipnir"] = "echo" } : extensions.ToDictionary(e => e.Name, e => e.Mode);
        var home = await StartAsync(extensions: map);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (home.Extensions!.Get("sleipnir") is { State: not Bifrost.Server.Extensions.ExtensionState.Running })
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("sleipnir did not start");
            await Task.Delay(20);
        }
        return home;
    }

    private static Dictionary<string, string[]> Headers(JsonElement echo) =>
        echo.GetProperty("headers").EnumerateObject()
            .ToDictionary(h => h.Name, h => h.Value.EnumerateArray().Select(v => v.GetString()!).ToArray(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Forwards_method_path_query_and_body()
    {
        await using var home = await HomeAsync();

        var echo = await Json(await home.SendAsync(HttpMethod.Post, "/x/sleipnir/api/v1/downloads?a=1", """{"urls":["http://e.test/a"]}"""));

        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.Equal("/api/v1/downloads", echo.GetProperty("path").GetString());
        Assert.Equal("?a=1", echo.GetProperty("query").GetString());
        Assert.Equal(28, echo.GetProperty("bodyLength").GetInt32());
    }

    [Fact]
    public async Task Adds_the_callers_identity()
    {
        await using var home = await HomeAsync();
        var (bob, token) = await home.AddPersonAsync("bob");

        var headers = Headers(await Json(await home.SendAsync(HttpMethod.Get, "/x/sleipnir/hello", token: token)));

        Assert.Equal([bob.PersonId.ToString()], headers["X-Bifrost-Person-Id"]);
        Assert.Equal(["member"], headers["X-Bifrost-Role"]);
        Assert.Equal([bob.DeviceId.ToString()], headers["X-Bifrost-Device-Id"]);
        Assert.Equal(["app"], headers["X-Bifrost-Device-Kind"]);
        Assert.Equal(["/x/sleipnir"], headers["X-Forwarded-Prefix"]);
    }

    [Fact]
    public async Task A_browser_fallback_device_is_sent_as_such()
    {
        await using var home = await HomeAsync();
        var (_, token) = await home.AddPersonAsync("tv", kind: DeviceKind.BrowserFallback);

        var headers = Headers(await Json(await home.SendAsync(HttpMethod.Get, "/x/sleipnir/hello", token: token)));

        Assert.Equal(["browser-fallback"], headers["X-Bifrost-Device-Kind"]);
    }

    [Fact]
    public async Task Client_identity_headers_and_credentials_never_reach_the_extension()
    {
        await using var home = await HomeAsync();
        var (bob, token) = await home.AddPersonAsync("bob");
        var request = home.Request(HttpMethod.Get, "/x/sleipnir/hello", token: token);
        request.Headers.TryAddWithoutValidation("X-Bifrost-Role", "admin");
        request.Headers.TryAddWithoutValidation("x-bifrost-person-id", home.Admin.PersonId.ToString());
        request.Headers.TryAddWithoutValidation("X-Bifrost-Extra", "anything");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=1.2.3.4");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/evil");

        var headers = Headers(await Json(await home.Http.SendAsync(request)));

        Assert.Equal(["member"], headers["X-Bifrost-Role"]);
        Assert.Equal([bob.PersonId.ToString()], headers["X-Bifrost-Person-Id"]);
        Assert.Equal(["/x/sleipnir"], headers["X-Forwarded-Prefix"]);
        Assert.False(headers.ContainsKey("X-Bifrost-Extra"));
        foreach (var name in new[] { "Authorization", "Cookie", "Forwarded", "X-Forwarded-For" })
        {
            Assert.False(headers.ContainsKey(name), name);
        }
    }

    [Fact]
    public async Task The_route_needs_a_device_token()
    {
        await using var home = await HomeAsync();

        var response = await home.Http.GetAsync("/x/sleipnir/hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await Code(response));
    }

    [Fact]
    public async Task Path_tricks_never_escape_the_extension()
    {
        await using var home = await HomeAsync();
        var (_, member) = await home.AddPersonAsync("bob");

        var clientSide = await home.SendAsync(HttpMethod.Get, "/x/sleipnir/../api/v1/people", token: member); // HttpClient resolves the dots
        var serverSide = await RawGetAsync(home.Port, "/x/sleipnir/../../api/v1/people", member);               // Kestrel resolves them
        var upper = await home.SendAsync(HttpMethod.Get, "/x/SLEIPNIR/hello");
        var encoded = await Json(await home.SendAsync(HttpMethod.Get, "/x/sleipnir/a%2F..%2Fb"));

        Assert.Equal("extension_not_found", await Code(clientSide)); // became /x/api/…: no extension named "api"
        Assert.StartsWith("HTTP/1.1 403", serverSide, StringComparison.Ordinal); // became Bifröst's own /api/v1/people, judged as the member
        Assert.Equal("extension_not_found", await Code(upper));
        Assert.Equal("/a%2F..%2Fb", encoded.GetProperty("path").GetString());
    }

    /// <summary>Sends the path byte for byte, without the client normalising it first.</summary>
    private static async Task<string> RawGetAsync(int port, string path, string token)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {token}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task An_unknown_extension_is_404_and_one_that_is_not_running_503()
    {
        await using var home = await HomeAsync(("sleipnir", "echo"), ("saga", "hang"));

        var unknown = await home.SendAsync(HttpMethod.Get, "/x/nope/hello");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var starting = await home.SendAsync(HttpMethod.Get, "/x/saga/hello");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("extension_not_found", await Code(unknown));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, starting.StatusCode);
        Assert.Equal("extension_unavailable", await Code(starting));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Large_responses_stream()
    {
        await using var home = await HomeAsync();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0, received = 0;

        using var response = await home.Http.SendAsync(home.Request(HttpMethod.Get, "/x/sleipnir/big?mb=50"), HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[81920];
        for (int n; (n = await body.ReadAsync(buffer)) > 0;)
        {
            received += n;
            if (received % (5 * 1024 * 1024) < n) peak = Math.Max(peak, GC.GetTotalMemory(false) - before);
        }

        Assert.Equal(50L * 1024 * 1024, received);
        Assert.True(peak < 20 * 1024 * 1024, $"heap grew by {peak / 1024 / 1024} MB");
    }

    [Fact]
    public async Task A_client_that_goes_away_cancels_the_upstream_request()
    {
        await using var home = await HomeAsync();
        var log = Path.Combine(home.Extensions!.Get("sleipnir")!.DataDir, "slow.log");

        using (var cts = new CancellationTokenSource())
        {
            var response = await home.Http.SendAsync(home.Request(HttpMethod.Get, "/x/sleipnir/slow"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await response.Content.ReadAsStreamAsync(cts.Token);
            await body.ReadExactlyAsync(new byte[1], cts.Token);
            await cts.CancelAsync();
            response.Dispose();
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(log) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal("cancelled", await File.ReadAllTextAsync(log));
    }

    [Fact]
    public async Task Bifrosts_own_routes_still_work_while_an_extension_is_down()
    {
        await using var home = await HomeAsync(("sleipnir", "echo"), ("saga", "crash"));

        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task The_extension_cannot_set_cookies_on_the_client()
    {
        await using var home = await HomeAsync();

        var response = await home.SendAsync(HttpMethod.Get, "/x/sleipnir/hello");

        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
