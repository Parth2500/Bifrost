using System.Net;
using Bifrost.Core.Models;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Server;

public class PeopleEndpointTests
{
    [Fact]
    public async Task The_admin_adds_a_member()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = "Bob" }));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/people/{json.GetProperty("id").GetGuid()}", response.Headers.Location?.ToString());
        Assert.Equal("Bob", json.GetProperty("displayName").GetString());
        Assert.Equal("member", json.GetProperty("role").GetString());
        Assert.Equal(2, (await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/people"))).GetArrayLength());
    }

    [Fact]
    public async Task Bad_and_duplicate_names_are_refused()
    {
        await using var home = await StartAsync();
        await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = "Bob" }));

        var dupe = await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = " bob " }));
        var bad = await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = "a/b" }));
        var missing = await home.SendAsync(HttpMethod.Post, "/api/v1/people", "{}");

        Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);
        Assert.Equal("name_taken", await Code(dupe));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_name", await Code(bad));
        Assert.Equal("invalid_name", await Code(missing));
    }

    [Fact]
    public async Task An_unknown_role_is_an_invalid_request()
    {
        await using var home = await StartAsync();

        var response = await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = "eve", role = "root" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await Code(response));
    }

    [Fact]
    public async Task A_member_cannot_shut_down_or_manage_people()
    {
        await using var home = await StartAsync();
        var (bob, token) = await home.AddPersonAsync("bob");

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Post, "/api/v1/server/shutdown", null),
            (HttpMethod.Get, "/api/v1/people", null),
            (HttpMethod.Post, "/api/v1/people", Body(new { displayName = "eve", role = "admin" })),
            (HttpMethod.Patch, $"/api/v1/people/{bob.PersonId}", Body(new { role = "admin" })),
            (HttpMethod.Delete, $"/api/v1/people/{home.Admin.PersonId}?confirm=true", null),
        })
        {
            var response = await home.SendAsync(method, path, body, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("forbidden", await Code(response));
        }

        Assert.Equal(PersonRole.Member, (await home.People.GetPersonAsync(bob.PersonId))!.Role);
        Assert.Equal(2, (await home.People.ListPeopleAsync()).Count);
        await Assert.ThrowsAsync<TimeoutException>(() => home.Host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task People_can_be_renamed_and_promoted()
    {
        await using var home = await StartAsync();
        var (bob, _) = await home.AddPersonAsync("bob");

        var response = await home.SendAsync(HttpMethod.Patch, $"/api/v1/people/{bob.PersonId}", Body(new { displayName = "Robert", role = "admin" }));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Robert", json.GetProperty("displayName").GetString());
        Assert.Equal("admin", json.GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_or_deleted()
    {
        await using var home = await StartAsync();

        var demote = await home.SendAsync(HttpMethod.Patch, $"/api/v1/people/{home.Admin.PersonId}", Body(new { role = "member" }));
        var delete = await home.SendAsync(HttpMethod.Delete, $"/api/v1/people/{home.Admin.PersonId}?confirm=true");

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await Code(demote));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("last_admin", await Code(delete));
    }

    [Fact]
    public async Task Deleting_a_person_needs_confirmation()
    {
        await using var home = await StartAsync();
        var (bob, _) = await home.AddPersonAsync("bob");

        var unconfirmed = await home.SendAsync(HttpMethod.Delete, $"/api/v1/people/{bob.PersonId}");

        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("confirmation_required", await Code(unconfirmed));
        Assert.NotNull(await home.People.GetPersonAsync(bob.PersonId));
    }

    [Fact]
    public async Task Unknown_people_are_a_404()
    {
        await using var home = await StartAsync();

        var delete = await home.SendAsync(HttpMethod.Delete, $"/api/v1/people/{Guid.NewGuid()}?confirm=true");
        var patch = await home.SendAsync(HttpMethod.Patch, $"/api/v1/people/{Guid.NewGuid()}", Body(new { displayName = "x" }));

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("person_not_found", await Code(delete));
        Assert.Equal("person_not_found", await Code(patch));
    }

    [Fact]
    public async Task Changes_through_the_API_are_audited_as_the_calling_device()
    {
        await using var home = await StartAsync();

        await home.SendAsync(HttpMethod.Post, "/api/v1/people", Body(new { displayName = "Bob" }));

        await using var db = await home.Database.CreateDbContextAsync();
        var row = db.Audit.OrderBy(a => a.Id).Last();
        Assert.Equal("person.created", row.Kind);
        Assert.Equal(home.Admin.DeviceId, row.ActorDeviceId);
    }

    [Fact]
    public async Task With_a_second_admin_the_host_admin_still_cannot_remove_itself()
    {
        await using var home = await StartAsync();
        await home.AddPersonAsync("bob", PersonRole.Admin);

        var delete = await home.SendAsync(HttpMethod.Delete, $"/api/v1/people/{home.Admin.PersonId}?confirm=true");
        var demote = await home.SendAsync(HttpMethod.Patch, $"/api/v1/people/{home.Admin.PersonId}", Body(new { role = "member" }));

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("local_admin", await Code(delete));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("local_admin", await Code(demote));
        Assert.Equal(HttpStatusCode.OK, (await home.SendAsync(HttpMethod.Get, "/api/v1/people")).StatusCode);
    }

    [Fact]
    public async Task Times_are_sent_as_UTC_on_every_route()
    {
        await using var home = await StartAsync();
        var (_, token) = await home.AddPersonAsync("bob");
        await home.SendAsync(HttpMethod.Get, "/api/v1/me", token: token); // sets last seen

        var people = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/people"));
        var devices = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/devices"));

        Assert.All(people.EnumerateArray(), p => Assert.EndsWith("Z", p.GetProperty("createdAt").GetString(), StringComparison.Ordinal));
        Assert.All(devices.EnumerateArray(), d => Assert.EndsWith("Z", d.GetProperty("createdAt").GetString(), StringComparison.Ordinal));
        Assert.Contains(devices.EnumerateArray(), d => d.GetProperty("lastSeenAt").GetString()?.EndsWith('Z') == true);
    }
}
