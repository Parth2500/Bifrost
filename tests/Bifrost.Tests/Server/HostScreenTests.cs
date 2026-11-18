using System.Net;
using Bifrost.Core.Models;
using Bifrost.Server.Security;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Server;

/// <summary>The host screen's sign-in (ADR 003): a link from the admin socket, a session cookie on loopback.</summary>
public sealed class HostScreenTests
{
    /// <summary>A browser: no redirects followed, cookies kept by hand so each test sees exactly what was sent.</summary>
    private static HttpClient Browser(HostFixture home) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{home.Port}"),
    };

    private static async Task<string> LinkAsync(HostFixture home)
    {
        var response = await home.AdminHttp.PostAsync("/api/v1/host/login-link", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Uri((await Json(response)).GetProperty("url").GetString()!).PathAndQuery;
    }

    private static async Task<string> SignInAsync(HostFixture home, HttpClient browser)
    {
        var response = await browser.GetAsync(await LinkAsync(home));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        return cookie.Split(';')[0]; // name=value
    }

    private static HttpRequestMessage With(string cookie, HttpMethod method, string path, string? origin = null, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task Only_the_admin_socket_can_ask_for_a_login_link()
    {
        await using var home = await StartAsync();

        var overHttp = await home.SendAsync(HttpMethod.Post, "/api/v1/host/login-link"); // even with the admin's own token
        var overSocket = await home.AdminHttp.PostAsync("/api/v1/host/login-link", null);

        Assert.Equal(HttpStatusCode.Forbidden, overHttp.StatusCode);
        Assert.Equal("host_only", await Code(overHttp));
        Assert.Equal(HttpStatusCode.OK, overSocket.StatusCode);
        Assert.StartsWith($"http://127.0.0.1:{home.Port}/host/login?t=", (await Json(overSocket)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_link_opens_a_session_cookie_that_the_page_cannot_read_and_other_sites_cannot_send()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);

        var response = await browser.GetAsync(await LinkAsync(home));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/host/", response.Headers.Location!.OriginalString); // the secret leaves the address bar
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("bifrost_host=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // a session cookie: gone when the browser closes
    }

    [Fact]
    public async Task A_link_works_once()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var path = await LinkAsync(home);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync(path)).StatusCode);
        var second = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains("bifrost open", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_link_expires()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var path = await LinkAsync(home);

        home.Time.Advance(HostSessions.LinkLife);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Wrong_guesses_are_slowed_but_a_good_link_always_works_so_nobody_can_lock_the_admin_out()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var good = await LinkAsync(home);

        for (var i = 0; i < 21; i++) await browser.GetAsync("/host/login?t=guess" + i);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.GetAsync("/host/login?t=one-more")).StatusCode); // guessing is slowed
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync(good)).StatusCode); // the real link is not
    }

    [Fact]
    public async Task The_session_opens_the_host_api_only_and_never_an_extension_page_that_shares_its_origin()
    {
        await using var home = await StartAsync(extensions: new Dictionary<string, string> { ["sleipnir"] = "echo" });
        await home.WaitForExtensionsAsync();
        using var browser = Browser(home);
        var cookie = await SignInAsync(home, browser);

        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(With(cookie, HttpMethod.Get, "/api/v1/people"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(With(cookie, HttpMethod.Get, "/x/sleipnir/"))).StatusCode);
    }

    [Fact]
    public async Task The_session_acts_as_the_admins_own_device()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var cookie = await SignInAsync(home, browser);

        var me = await Json(await browser.SendAsync(With(cookie, HttpMethod.Get, "/api/v1/me")));

        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.Equal("This computer", me.GetProperty("deviceName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(With(cookie, HttpMethod.Get, "/api/v1/people"))).StatusCode);
    }

    [Fact]
    public async Task Without_the_cookie_or_a_token_the_api_stays_closed()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/people")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(With("bifrost_host=not-a-session", HttpMethod.Get, "/api/v1/people"))).StatusCode);
    }

    [Fact]
    public async Task A_change_must_come_from_the_host_screens_own_origin()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var cookie = await SignInAsync(home, browser);
        var body = Body(new { displayName = "Aanya", role = "member" });

        var noOrigin = await browser.SendAsync(With(cookie, HttpMethod.Post, "/api/v1/people", body: body));
        var foreign = await browser.SendAsync(With(cookie, HttpMethod.Post, "/api/v1/people", "http://evil.example", body));
        var own = await browser.SendAsync(With(cookie, HttpMethod.Post, "/api/v1/people", $"http://127.0.0.1:{home.Port}", body));

        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        Assert.Equal("cross_site", await Code(noOrigin));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task Closing_the_screen_ends_the_session_and_an_idle_hour_does_too()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);
        var cookie = await SignInAsync(home, browser);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(With(cookie, HttpMethod.Post, "/api/v1/host/logout", $"http://127.0.0.1:{home.Port}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(With(cookie, HttpMethod.Get, "/api/v1/people"))).StatusCode);

        var again = await SignInAsync(home, browser);
        home.Time.Advance(HostSessions.IdleLife);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(With(again, HttpMethod.Get, "/api/v1/people"))).StatusCode);
    }

    [Fact]
    public async Task The_screen_is_served_only_on_loopback_with_a_strict_policy_and_no_inline_script()
    {
        await using var home = await StartAsync();
        using var browser = Browser(home);

        var page = await browser.GetAsync("/host/");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("<script>", html); // only <script src=…>
        Assert.Equal("image/png", (await browser.GetAsync("/host/mark.png")).Content.Headers.ContentType!.MediaType);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/host/");
        rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(rebinding)).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_start_or_stop_an_extension_and_the_admin_can()
    {
        await using var home = await StartAsync(extensions: new Dictionary<string, string> { ["sleipnir"] = "echo" });
        await home.WaitForExtensionsAsync();
        var (_, token) = await home.AddPersonAsync("bob", PersonRole.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await home.SendAsync(HttpMethod.Post, "/api/v1/extensions/sleipnir/stop", token: token)).StatusCode);
        var (_, remoteAdmin) = await home.AddPersonAsync("carol", PersonRole.Admin); // an admin on her phone is not "this computer"
        Assert.Equal(HttpStatusCode.Forbidden, (await home.SendAsync(HttpMethod.Post, "/api/v1/extensions/sleipnir/stop", token: remoteAdmin)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await home.AdminHttp.PostAsync("/api/v1/extensions/sleipnir/stop", null)).StatusCode);
        Assert.Equal(Bifrost.Server.Extensions.ExtensionState.Stopped, home.Extensions!.Get("sleipnir")!.State);
        Assert.Equal(HttpStatusCode.Accepted, (await home.AdminHttp.PostAsync("/api/v1/extensions/sleipnir/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await home.AdminHttp.PostAsync("/api/v1/extensions/sleipnir/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await home.AdminHttp.PostAsync("/api/v1/extensions/nope/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await home.AdminHttp.PostAsync("/api/v1/extensions/sleipnir/explode", null)).StatusCode);
    }

    [Fact]
    public async Task The_home_info_is_for_this_computer_only()
    {
        await using var home = await StartAsync();

        var (_, remoteAdmin) = await home.AddPersonAsync("carol", PersonRole.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await home.SendAsync(HttpMethod.Get, "/api/v1/host/info", token: remoteAdmin)).StatusCode);
        var info = await Json(await home.AdminHttp.GetAsync("/api/v1/host/info"));
        Assert.Equal(home.Port, info.GetProperty("loopbackPort").GetInt32());
    }

    [Fact]
    public async Task An_invitation_can_come_back_as_a_picture_of_its_code()
    {
        await using var home = await StartAsync();
        var (principal, _) = await home.AddPersonAsync("aanya");

        var off = await home.AdminHttp.PostAsync("/api/v1/invitations?qr=svg", new StringContent(Body(new { personId = principal.PersonId }), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode); // no home network in this fixture: the same answer as before
    }
}
