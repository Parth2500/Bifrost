using Bifrost.Core.Models;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Server;

public sealed class PeopleServiceTests : IDisposable
{
    private readonly BifrostDbFactory _factory = Database.OpenMemory();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly PeopleService _people;

    public PeopleServiceTests()
    {
        Database.MigrateAsync(_factory).GetAwaiter().GetResult();
        _people = new PeopleService(_factory, _time);
    }

    public void Dispose() => _factory.Dispose();

    private Task<Principal> AdminAsync() => _people.EnsureLocalAdminAsync("alice", "local-token");

    private static async Task<string> CodeOf(Task task)
    {
        var e = await Assert.ThrowsAsync<PeopleException>(() => task);
        return e.Code;
    }

    private async Task<List<AuditEntity>> AuditAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Audit.OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task The_first_start_creates_an_admin_and_the_local_device()
    {
        var principal = await AdminAsync();

        Assert.Equal("alice", principal.PersonName);
        Assert.Equal(PersonRole.Admin, principal.Role);
        Assert.True(principal.IsLocal);
        Assert.Equal(DeviceKind.App, principal.Kind);
        Assert.Equal("This computer", principal.DeviceName);
        Assert.Equal(principal, await _people.AuthenticateAsync("local-token"));
        Assert.Single(await _people.ListPeopleAsync());
        Assert.Single(await _people.ListDevicesAsync(null));
    }

    [Fact]
    public async Task A_regenerated_local_token_replaces_the_old_one_without_new_people()
    {
        var first = await _people.EnsureLocalAdminAsync("alice", "old-token");

        var second = await _people.EnsureLocalAdminAsync("alice", "new-token");

        Assert.Equal(first.PersonId, second.PersonId);
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Null(await _people.AuthenticateAsync("old-token"));
        Assert.NotNull(await _people.AuthenticateAsync("new-token"));
        Assert.Single(await _people.ListPeopleAsync());
        Assert.Single(await _people.ListDevicesAsync(null));
    }

    [Fact]
    public async Task Unknown_empty_and_revoked_tokens_do_not_authenticate()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);
        var issued = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);

        Assert.NotNull(await _people.AuthenticateAsync(issued.Token));
        Assert.Null(await _people.AuthenticateAsync("nope"));
        Assert.Null(await _people.AuthenticateAsync(""));
        Assert.True(await _people.RevokeDeviceAsync(admin, issued.Device.Id));
        Assert.Null(await _people.AuthenticateAsync(issued.Token));
        Assert.False(await _people.RevokeDeviceAsync(admin, Guid.NewGuid()));
    }

    [Fact]
    public async Task Issued_tokens_are_43_character_base64url_and_unique()
    {
        var bob = await _people.CreatePersonAsync(await AdminAsync(), "bob", PersonRole.Member);

        var a = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);
        var b = await _people.IssueDeviceAsync(bob.Id, "tablet", "android", DeviceKind.App);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Token);
        Assert.NotEqual(a.Token, b.Token);
    }

    [Fact]
    public async Task Issuing_a_device_for_a_missing_person_is_refused()
    {
        Assert.Equal("person_not_found", await CodeOf(_people.IssueDeviceAsync(Guid.NewGuid(), "phone", "android", DeviceKind.App)));
    }

    [Fact]
    public async Task Last_seen_is_updated_at_most_once_a_minute()
    {
        var bob = await _people.CreatePersonAsync(await AdminAsync(), "bob", PersonRole.Member);
        var issued = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);
        Assert.Null((await _people.GetDeviceAsync(issued.Device.Id))!.LastSeenAt);

        await _people.AuthenticateAsync(issued.Token);
        var first = (await _people.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;
        _time.Advance(TimeSpan.FromSeconds(59));
        await _people.AuthenticateAsync(issued.Token);
        var within = (await _people.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;
        _time.Advance(TimeSpan.FromSeconds(1));
        await _people.AuthenticateAsync(issued.Token);
        var after = (await _people.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;

        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddSeconds(-60), first);
        Assert.Equal(first, within);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, after);
    }

    [Fact]
    public async Task Only_the_hash_is_stored_never_the_token()
    {
        var bob = await _people.CreatePersonAsync(await AdminAsync(), "bob", PersonRole.Member);
        var issued = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);

        await using var db = await _factory.CreateDbContextAsync();
        var device = await db.Devices.SingleAsync(d => d.Id == issued.Device.Id);

        Assert.Equal(PeopleService.HashToken(issued.Token), device.TokenHash);
        Assert.Matches("^[0-9a-f]{64}$", device.TokenHash);
        var audit = string.Join('|', (await db.Audit.ToListAsync()).Select(a => a.Detail));
        Assert.DoesNotContain(issued.Token, audit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\u0001b")]
    [InlineData("a\0b")]
    public async Task Bad_names_are_rejected(string name)
    {
        Assert.Equal("invalid_name", await CodeOf(_people.CreatePersonAsync(await AdminAsync(), name, PersonRole.Member)));
    }

    [Fact]
    public async Task Names_are_limited_to_40_characters_and_trimmed()
    {
        var admin = await AdminAsync();
        Assert.Equal("invalid_name", await CodeOf(_people.CreatePersonAsync(admin, new string('x', 41), PersonRole.Member)));

        var created = await _people.CreatePersonAsync(admin, "  Bob  ", PersonRole.Member);

        Assert.Equal("Bob", created.DisplayName);
    }

    [Fact]
    public async Task Names_that_differ_by_case_or_spaces_are_taken()
    {
        var admin = await AdminAsync();
        await _people.CreatePersonAsync(admin, "Bob", PersonRole.Member);

        Assert.Equal("name_taken", await CodeOf(_people.CreatePersonAsync(admin, " bob ", PersonRole.Member)));
        Assert.Equal(2, (await _people.ListPeopleAsync()).Count);
    }

    [Theory]
    [InlineData("Émile", "émile")]
    [InlineData("émile", "e\u0301mile")]
    [InlineData("ÄNNE", "änne")]
    [InlineData("ﬁona", "fiona")]
    public async Task Names_that_differ_by_unicode_case_or_form_are_taken(string first, string second)
    {
        var admin = await AdminAsync();
        await _people.CreatePersonAsync(admin, first, PersonRole.Member);

        Assert.Equal("name_taken", await CodeOf(_people.CreatePersonAsync(admin, second, PersonRole.Member)));
    }

    [Theory]
    [InlineData("Bob\u200B")]
    [InlineData("\u202Eevil")]
    [InlineData("a\uFEFFb")]
    public async Task Names_with_invisible_format_characters_are_rejected(string name)
    {
        Assert.Equal("invalid_name", await CodeOf(_people.CreatePersonAsync(await AdminAsync(), name, PersonRole.Member)));
    }

    [Fact]
    public async Task Renaming_onto_another_persons_name_is_taken()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);

        Assert.Equal("name_taken", await CodeOf(_people.UpdatePersonAsync(admin, bob.Id, "ALICE", null)));
    }

    [Fact]
    public async Task The_only_admin_cannot_be_demoted()
    {
        var admin = await AdminAsync();

        Assert.Equal("last_admin", await CodeOf(_people.UpdatePersonAsync(admin, admin.PersonId, null, PersonRole.Member)));
        Assert.Equal(PersonRole.Admin, (await _people.GetPersonAsync(admin.PersonId))!.Role);
    }

    [Fact]
    public async Task Deleting_the_last_admin_is_refused_and_changes_nothing()
    {
        var admin = await AdminAsync();

        Assert.Equal("last_admin", await CodeOf(_people.DeletePersonAsync(admin, admin.PersonId)));

        Assert.NotNull(await _people.GetPersonAsync(admin.PersonId));
        Assert.NotNull(await _people.AuthenticateAsync("local-token"));
    }

    [Fact]
    public async Task With_two_admins_one_can_be_demoted()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Admin);

        var updated = await _people.UpdatePersonAsync(admin, bob.Id, null, PersonRole.Member);

        Assert.Equal(PersonRole.Member, updated.Role);
    }

    public enum Change { Demote, Delete }

    /// <summary>
    /// Two admins and no local device, so only the last-admin rule stands between them. Each round starts
    /// both changes on pool threads behind a barrier, so they really overlap; without the gate this fails.
    /// </summary>
    [Theory]
    [InlineData(Change.Demote, Change.Demote)]
    [InlineData(Change.Delete, Change.Delete)]
    [InlineData(Change.Delete, Change.Demote)]
    public async Task Two_concurrent_changes_to_the_two_admins_leave_one_admin(Change first, Change second)
    {
        var someone = new Principal(Guid.NewGuid(), "someone", PersonRole.Admin, Guid.NewGuid(), "test", DeviceKind.App, false);
        for (var round = 0; round < 50; round++)
        {
            using var factory = Database.OpenMemory();
            await Database.MigrateAsync(factory);
            using var people = new PeopleService(factory, TimeProvider.System);
            var a = await people.CreatePersonAsync(someone, "a", PersonRole.Admin);
            var b = await people.CreatePersonAsync(someone, "b", PersonRole.Admin);
            using var barrier = new Barrier(2);

            var results = await Task.WhenAll(
                Task.Run(() => Attempt(barrier, () => Apply(people, someone, a.Id, first))),
                Task.Run(() => Attempt(barrier, () => Apply(people, someone, b.Id, second))));

            Assert.Equal(1, results.Count(ok => ok));
            Assert.Equal(1, (await people.ListPeopleAsync()).Count(p => p.Role == PersonRole.Admin));
        }

        static async Task<bool> Attempt(Barrier barrier, Func<Task> change)
        {
            barrier.SignalAndWait();
            try { await change(); return true; }
            catch (PeopleException e) when (e.Code == "last_admin") { return false; }
        }
    }

    private static Task Apply(PeopleService people, Principal actor, Guid id, Change change) => change == Change.Demote
        ? people.UpdatePersonAsync(actor, id, null, PersonRole.Member)
        : people.DeletePersonAsync(actor, id);

    [Fact]
    public async Task The_host_admin_cannot_be_demoted_or_deleted_even_with_another_admin()
    {
        var admin = await AdminAsync();
        await _people.CreatePersonAsync(admin, "bob", PersonRole.Admin);
        var before = (await AuditAsync()).Count;

        Assert.Equal("local_admin", await CodeOf(_people.UpdatePersonAsync(admin, admin.PersonId, null, PersonRole.Member)));
        Assert.Equal("local_admin", await CodeOf(_people.DeletePersonAsync(admin, admin.PersonId)));

        Assert.Equal(PersonRole.Admin, (await _people.GetPersonAsync(admin.PersonId))!.Role);
        Assert.NotNull(await _people.AuthenticateAsync("local-token"));
        Assert.Equal(before, (await AuditAsync()).Count);
    }

    [Fact]
    public async Task The_host_admin_can_still_be_renamed()
    {
        var admin = await AdminAsync();

        Assert.Equal("Alicia", (await _people.UpdatePersonAsync(admin, admin.PersonId, "Alicia", null)).DisplayName);
    }

    [Fact]
    public async Task The_local_device_cannot_be_revoked()
    {
        var admin = await AdminAsync();

        Assert.Equal("local_device", await CodeOf(_people.RevokeDeviceAsync(admin, admin.DeviceId)));
        Assert.NotNull(await _people.AuthenticateAsync("local-token"));
    }

    [Fact]
    public async Task Deleting_a_person_removes_their_devices_and_tokens()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);
        var phone = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);

        await _people.DeletePersonAsync(admin, bob.Id);

        Assert.Null(await _people.AuthenticateAsync(phone.Token));
        Assert.Null(await _people.GetPersonAsync(bob.Id));
        Assert.Null(await _people.GetDeviceAsync(phone.Device.Id));
        Assert.Equal("person_not_found", await CodeOf(_people.DeletePersonAsync(admin, bob.Id)));
    }

    [Fact]
    public async Task Every_change_is_audited_with_its_actor()
    {
        var admin = await AdminAsync();
        var before = (await AuditAsync()).Count;

        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);
        await _people.UpdatePersonAsync(admin, bob.Id, "robert", null);
        await _people.UpdatePersonAsync(admin, bob.Id, null, PersonRole.Admin);
        var phone = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);
        await _people.RevokeDeviceAsync(admin, phone.Device.Id);
        await _people.DeletePersonAsync(admin, bob.Id);

        var rows = (await AuditAsync()).Skip(before).ToList();
        Assert.Equal(
            ["person.created", "person.renamed", "person.role_changed", "device.issued", "device.revoked", "person.deleted"],
            rows.Select(r => r.Kind));
        Assert.Equal("member->admin", rows[2].Detail);
        Assert.All(rows.Where(r => r.Kind != "device.issued"), r =>
        {
            Assert.Equal(admin.PersonId, r.ActorPersonId);
            Assert.Equal(admin.DeviceId, r.ActorDeviceId);
        });
        Assert.Equal([bob.Id, bob.Id, bob.Id, phone.Device.Id, phone.Device.Id, bob.Id], rows.Select(r => r.SubjectId!.Value));
        Assert.All(rows, r => Assert.Equal(_time.GetUtcNow().UtcDateTime, r.At));
    }

    [Fact]
    public async Task A_refused_change_writes_no_audit_row()
    {
        var admin = await AdminAsync();
        var before = (await AuditAsync()).Count;

        await CodeOf(_people.UpdatePersonAsync(admin, admin.PersonId, null, PersonRole.Member));
        await CodeOf(_people.DeletePersonAsync(admin, admin.PersonId));
        await CodeOf(_people.CreatePersonAsync(admin, "ALICE", PersonRole.Member));

        Assert.Equal(before, (await AuditAsync()).Count);
    }

    [Fact]
    public async Task Revoking_twice_is_audited_once()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);
        var phone = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);

        await _people.RevokeDeviceAsync(admin, phone.Device.Id);
        await _people.RevokeDeviceAsync(admin, phone.Device.Id);

        Assert.Single(await AuditAsync(), r => r.Kind == "device.revoked");
    }

    [Fact]
    public async Task Times_read_back_from_the_store_are_UTC()
    {
        var admin = await AdminAsync();
        var bob = await _people.CreatePersonAsync(admin, "bob", PersonRole.Member);
        var phone = await _people.IssueDeviceAsync(bob.Id, "phone", "android", DeviceKind.App);
        await _people.AuthenticateAsync(phone.Token);

        var person = (await _people.GetPersonAsync(bob.Id))!;
        var device = (await _people.GetDeviceAsync(phone.Device.Id))!;

        Assert.Equal(DateTimeKind.Utc, person.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, device.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, device.LastSeenAt!.Value.Kind);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, device.LastSeenAt);
    }
}
