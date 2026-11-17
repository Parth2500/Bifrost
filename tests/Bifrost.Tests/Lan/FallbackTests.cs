using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Bifrost.Server.Hosting;
using Bifrost.Server.Security;
using Bifrost.Tests.Server;

namespace Bifrost.Tests.Lan;

public class FallbackTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string Commit(string nonce) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nonce)));

    [Fact]
    public async Task A_browser_paired_on_the_fallback_can_look_but_not_change_and_app_tokens_are_kept_off_it()
    {
        using var key = HomeKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "bifrost-fb-" + Guid.NewGuid().ToString("N")[..8]));
        var plain = FreePort();
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, 0, plain));
        home.Http.DefaultRequestHeaders.Authorization = new("Bearer", home.Token);
        using var browser = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{plain}") };
        const string nonce = "a-browser-nonce-long-enough";

        var started = await (await browser.PostAsJsonAsync("/api/v1/join", new { name = "Laptop browser", platform = "web", commit = Commit(nonce) })).Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("requestId").GetGuid();
        await browser.PostAsJsonAsync($"/api/v1/join/{id}/reveal", new { nonce });
        await home.Http.PostAsJsonAsync($"/api/v1/join-requests/{id}/approve", new { personId = home.Admin.PersonId });
        var token = (await (await browser.PostAsJsonAsync($"/api/v1/join/{id}/collect", new { nonce })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        browser.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/api/v1/invitations", new { personId = home.Admin.PersonId })).StatusCode);

        using var pairOnPlain = await browser.PostAsJsonAsync("/api/v1/pair", new { secret = "x", name = "x", platform = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, pairOnPlain.StatusCode); // invitation secrets stay off plain HTTP

        using var app = new HttpClient { BaseAddress = browser.BaseAddress };
        app.DefaultRequestHeaders.Authorization = new("Bearer", home.Token); // an app token must never cross plain HTTP
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Starting_join_requests_is_rate_limited()
    {
        using var key = HomeKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "bifrost-rl-" + Guid.NewGuid().ToString("N")[..8]));
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, 0));
        using var device = new HttpClient { BaseAddress = home.Http.BaseAddress };
        var last = HttpStatusCode.OK;
        for (var i = 0; i < 12; i++)
        {
            last = (await device.PostAsJsonAsync(i % 2 == 0 ? "/api/v1/join" : "/api/v1/join/", new { name = "x", platform = "y", commit = Commit("n" + i) })).StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
