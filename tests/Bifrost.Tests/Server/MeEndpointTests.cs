using System.Net;
using Bifrost.Core.Models;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Server;

public class MeEndpointTests
{
    [Fact]
    public async Task The_local_token_is_the_admin_on_this_computer()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Get, "/api/v1/me");
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", json.GetProperty("personName").GetString());
        Assert.Equal("admin", json.GetProperty("role").GetString());
        Assert.Equal("This computer", json.GetProperty("deviceName").GetString());
        Assert.Equal("app", json.GetProperty("kind").GetString());
        Assert.Equal(home.Admin.PersonId, json.GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task A_member_token_is_a_member()
    {
        await using var home = await StartAsync();
        var (bob, token) = await home.AddPersonAsync("bob");

        var json = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token));

        Assert.Equal("bob", json.GetProperty("personName").GetString());
        Assert.Equal("member", json.GetProperty("role").GetString());
        Assert.Equal(bob.DeviceId, json.GetProperty("deviceId").GetGuid());
    }

    [Fact]
    public async Task Me_reports_the_device_kind()
    {
        await using var home = await StartAsync();
        var (_, token) = await home.AddPersonAsync("tv", kind: DeviceKind.BrowserFallback);

        var json = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token));

        Assert.Equal("browser-fallback", json.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_revoked_device_is_refused_on_its_next_request()
    {
        await using var home = await StartAsync();
        var (bob, token) = await home.AddPersonAsync("bob");
        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{bob.DeviceId}")).StatusCode);

        var after = await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("unauthorized", await Code(after));
    }

    [Fact]
    public async Task Deleting_a_person_refuses_their_devices_at_once()
    {
        await using var home = await StartAsync();
        var (bob, token) = await home.AddPersonAsync("bob");
        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await home.SendAsync(HttpMethod.Delete, $"/api/v1/people/{bob.PersonId}?confirm=true")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token)).StatusCode);
    }
}
