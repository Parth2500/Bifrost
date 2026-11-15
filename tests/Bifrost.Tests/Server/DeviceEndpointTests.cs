using System.Net;
using Bifrost.Server.People;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Server;

public class DeviceEndpointTests
{
    [Fact]
    public async Task Devices_are_listed_per_person_for_members_and_for_everyone_for_admins()
    {
        await using var home = await StartAsync();
        var (bob, bobToken) = await home.AddPersonAsync("bob");
        var (eve, _) = await home.AddPersonAsync("eve");

        var mine = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/devices", token: bobToken));
        var all = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/devices"));
        var filtered = await Json(await home.SendAsync(HttpMethod.Get, $"/api/v1/devices?person={eve.PersonId}"));
        var snoop = await home.SendAsync(HttpMethod.Get, $"/api/v1/devices?person={eve.PersonId}", token: bobToken);

        Assert.Equal([bob.DeviceId], mine.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal("app", mine[0].GetProperty("kind").GetString());
        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal([eve.DeviceId], filtered.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, snoop.StatusCode);
    }

    [Fact]
    public async Task A_device_can_be_revoked_by_its_owner_or_an_admin_but_not_by_others()
    {
        await using var home = await StartAsync();
        var (bob, bobToken) = await home.AddPersonAsync("bob");
        var (eve, eveToken) = await home.AddPersonAsync("eve");

        var stranger = await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{bob.DeviceId}", token: eveToken);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal("device_not_found", await Code(stranger));
        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: bobToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{bob.DeviceId}", token: bobToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: bobToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{eve.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: eveToken)).StatusCode);
    }

    [Fact]
    public async Task The_local_device_cannot_be_revoked()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Delete, $"/api/v1/devices/{home.Admin.DeviceId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("local_device", await Code(response));
        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_hashes_never_appear_in_responses()
    {
        await using var home = await StartAsync();
        var (_, token) = await home.AddPersonAsync("bob");
        var hash = PeopleService.HashToken(token);

        foreach (var path in new[] { "/api/v1/people", "/api/v1/devices", "/api/v1/me" })
        {
            var text = await (await home.SendAsync(HttpMethod.Get, path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            Assert.DoesNotContain("tokenHash", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
