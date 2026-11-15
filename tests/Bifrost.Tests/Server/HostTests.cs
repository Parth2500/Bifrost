using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bifrost.Server.Security;
using Microsoft.Extensions.Time.Testing;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Server;

public class HostTests
{
    [Fact]
    public async Task Server_info_is_public_and_says_nothing_else()
    {
        await using var home = await StartAsync();

        var response = await home.Http.GetAsync("/api/v1/server");
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bifröst", json.GetProperty("name").GetString());
        Assert.Equal(["v1"], json.GetProperty("apiVersions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["apiVersions", "name", "version"], json.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused_as_problem_json()
    {
        await using var home = await StartAsync();

        var response = await home.Http.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthorized", await Code(response));
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Wrong_tokens_of_any_shape_are_refused()
    {
        await using var home = await StartAsync();
        var sameLengthWrong = home.Token[..^1] + (home.Token[^1] == 'a' ? 'b' : 'a');

        foreach (var token in new[] { "wrong", sameLengthWrong, home.Token + "x" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token)).StatusCode);
        }

        var basic = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic " + home.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.Http.SendAsync(basic)).StatusCode);
    }

    [Fact]
    public async Task A_token_in_the_query_string_or_a_cookie_is_not_accepted()
    {
        await using var home = await StartAsync();

        var query = await home.Http.GetAsync($"/api/v1/me?access_token={home.Token}");
        var cookie = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        cookie.Headers.Add("Cookie", $"token={home.Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.Http.SendAsync(cookie)).StatusCode);
    }

    [Fact]
    public async Task Shutdown_by_the_admin_is_accepted_and_signals_the_host()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Post, "/api/v1/server/shutdown");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await home.Host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("example.com")]
    [InlineData("evil.example:7780")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.nip.io")]
    public async Task Hosts_that_only_look_local_are_refused_even_with_a_valid_token(string host)
    {
        await using var home = await StartAsync();
        var request = home.Request(HttpMethod.Get, "/api/v1/me");
        request.Headers.Host = host;

        var response = await home.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("host_not_allowed", await Code(response));
    }

    [Theory]
    [InlineData("localhost:7780")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Loopback_Host_headers_are_accepted(string host)
    {
        await using var home = await StartAsync();
        var request = home.Request(HttpMethod.Get, "/api/v1/me");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await home.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task No_response_carries_CORS_or_cookie_headers()
    {
        await using var home = await StartAsync();
        var simple = home.Request(HttpMethod.Get, "/api/v1/me");
        simple.Headers.Add("Origin", "https://evil.example");
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/people");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        foreach (var request in new[] { simple, preflight })
        {
            var response = await home.Http.SendAsync(request);
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task Wrong_tokens_are_throttled_but_the_right_one_still_works()
    {
        await using var home = await StartAsync();
        for (var i = 0; i < 20; i++)
        {
            await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: "wrong");
        }

        var guess = await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: "wrong");
        var owner = await home.SendAsync(HttpMethod.Get, "/api/v1/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, guess.StatusCode);
        Assert.Equal("too_many_attempts", await Code(guess));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        home.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Requests_without_credentials_do_not_use_up_the_failure_budget()
    {
        await using var home = await StartAsync();
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await home.Http.GetAsync("/api/v1/me")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: "wrong")).StatusCode);
    }

    [Fact]
    public void The_failure_limiter_forgets_old_failures()
    {
        var time = new FakeTimeProvider();
        var limiter = new AuthFailureLimiter(maxFailures: 3, window: TimeSpan.FromMinutes(1), time: time);
        for (var i = 0; i < 3; i++) limiter.RecordFailure("a");

        Assert.True(limiter.IsBlocked("a"));
        Assert.False(limiter.IsBlocked("b"));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(limiter.IsBlocked("a"));
    }

    [Fact]
    public async Task Unknown_api_paths_are_404_problem_json()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Get, "/api/v1/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await Code(response));
    }

    [Fact]
    public async Task The_API_is_described_by_OpenAPI()
    {
        await using var home = await StartAsync();

        var response = await home.Http.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await Json(response)).GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/api/v1/people", paths);
        Assert.Contains("/api/v1/devices/{id}", paths);
        Assert.Equal(HttpStatusCode.OK, (await home.Http.GetAsync("/scalar")).StatusCode);
    }

    [Fact]
    public async Task No_token_appears_in_the_log()
    {
        await using var home = await StartAsync();
        var (_, member) = await home.AddPersonAsync("bob");

        await home.SendAsync(HttpMethod.Get, "/api/v1/me");
        await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: member);
        await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: "wrong-" + member);
        await home.Http.GetAsync($"/api/v1/me?access_token={home.Token}");
        await home.SendAsync(HttpMethod.Post, "/api/v1/people", "{not json", token: member);
        await home.SendAsync(HttpMethod.Post, "/api/v1/people", "{not json");
        await home.SendAsync(HttpMethod.Get, "/api/v1/nope");

        Assert.NotEmpty(home.Logs.Lines);
        Assert.DoesNotContain(home.Logs.Lines, line => line.Contains(home.Token, StringComparison.Ordinal));
        Assert.DoesNotContain(home.Logs.Lines, line => line.Contains(member, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_busy_port_makes_the_host_take_the_next_free_one()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var home = await StartAsync(port: busy);

        Assert.NotEqual(busy, home.Port);
        Assert.InRange(home.Port, busy + 1, busy + 20);
        Assert.Equal(HttpStatusCode.OK, (await home.Http.GetAsync("/api/v1/server")).StatusCode);
    }

    [Fact]
    public async Task Ambient_Kestrel_configuration_cannot_open_extra_listeners()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var extra = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Environment.SetEnvironmentVariable("Kestrel__Endpoints__Extra__Url", $"http://127.0.0.1:{extra}");
        try
        {
            await using var home = await StartAsync();

            using var client = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, extra));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Extra__Url", null);
        }
    }

    [Fact]
    public async Task Stopping_does_not_wait_for_a_client_that_never_finishes_its_request()
    {
        await using var home = await StartAsync();
        using var stuck = new TcpClient();
        await stuck.ConnectAsync(IPAddress.Loopback, home.Port);
        var head = $"POST /api/v1/people HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {home.Token}\r\n"
                 + "Content-Type: application/json\r\nContent-Length: 1000\r\n\r\n{\"displayName\":";
        await stuck.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
        await Task.Delay(300);

        var clock = Stopwatch.StartNew();
        await home.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"stopping took {clock.Elapsed}");
    }
}
