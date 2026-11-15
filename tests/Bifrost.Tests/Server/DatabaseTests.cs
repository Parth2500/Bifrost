using Bifrost.Core.Models;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Tests.Server;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bifrost-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PersonEntity Person(string name, PersonRole role = PersonRole.Member) =>
        new() { Id = Guid.NewGuid(), DisplayName = name, NameKey = PersonNames.Key(name), Role = role, CreatedAt = DateTime.UtcNow };

    private static DeviceEntity Device(Guid personId, string hash = "hash", DeviceKind kind = DeviceKind.App) =>
        new() { Id = Guid.NewGuid(), PersonId = personId, Name = "phone", Platform = "android", Kind = kind, TokenHash = hash, CreatedAt = DateTime.UtcNow };

    private static async Task<BifrostDbFactory> MemoryAsync()
    {
        var factory = Database.OpenMemory();
        await Database.MigrateAsync(factory);
        return factory;
    }

    [Fact]
    public async Task A_new_database_file_is_owner_only_and_has_the_tables()
    {
        var path = Path.Combine(_dir, "bifrost.db");
        using var factory = Database.OpenFile(path);

        await Database.MigrateAsync(factory);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        await using var db = await factory.CreateDbContextAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();
        Assert.Contains("people", tables);
        Assert.Contains("devices", tables);
        Assert.Contains("audit", tables);
    }

    [Fact]
    public async Task Migrating_twice_is_harmless_and_keeps_data()
    {
        using var factory = Database.OpenFile(Path.Combine(_dir, "bifrost.db"));
        await Database.MigrateAsync(factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.People.Add(Person("alice"));
            await db.SaveChangesAsync();
        }

        await Database.MigrateAsync(factory);

        await using var again = await factory.CreateDbContextAsync();
        Assert.Equal(1, await again.People.CountAsync());
    }

    [Fact]
    public async Task Name_keys_are_unique()
    {
        using var factory = await MemoryAsync();
        await using var db = await factory.CreateDbContextAsync();
        db.People.Add(Person("Bob"));
        await db.SaveChangesAsync();

        db.People.Add(Person("bob"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Token_hashes_are_unique()
    {
        using var factory = await MemoryAsync();
        await using var db = await factory.CreateDbContextAsync();
        var person = Person("alice");
        db.People.Add(person);
        db.Devices.Add(Device(person.Id, "same"));
        await db.SaveChangesAsync();

        db.Devices.Add(Device(person.Id, "same"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_person_deletes_their_devices()
    {
        using var factory = await MemoryAsync();
        await using var db = await factory.CreateDbContextAsync();
        var person = Person("alice");
        db.People.Add(person);
        db.Devices.Add(Device(person.Id));
        await db.SaveChangesAsync();

        db.People.Remove(person);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task Role_and_kind_are_stored_as_the_designs_words()
    {
        using var factory = await MemoryAsync();
        await using var db = await factory.CreateDbContextAsync();
        var person = Person("alice");
        db.People.Add(person);
        db.Devices.Add(Device(person.Id, kind: DeviceKind.BrowserFallback));
        await db.SaveChangesAsync();

        Assert.Equal("member", await db.Database.SqlQueryRaw<string>("SELECT role AS Value FROM people").SingleAsync());
        Assert.Equal("browser-fallback", await db.Database.SqlQueryRaw<string>("SELECT kind AS Value FROM devices").SingleAsync());
        await using var fresh = await factory.CreateDbContextAsync();
        Assert.Equal(PersonRole.Member, (await fresh.People.SingleAsync()).Role);
        Assert.Equal(DeviceKind.BrowserFallback, (await fresh.Devices.SingleAsync()).Kind);
    }

    [Fact]
    public async Task Audit_rows_survive_deleting_the_person_they_name()
    {
        using var factory = await MemoryAsync();
        await using var db = await factory.CreateDbContextAsync();
        var person = Person("alice");
        db.People.Add(person);
        db.Audit.Add(new AuditEntity { At = DateTime.UtcNow, Kind = "person.created", SubjectId = person.Id });
        await db.SaveChangesAsync();

        db.People.Remove(person);
        await db.SaveChangesAsync();

        Assert.Equal(person.Id, (await db.Audit.SingleAsync()).SubjectId);
    }

    [Fact]
    public async Task A_widened_database_file_is_made_owner_only_on_open()
    {
        var path = Path.Combine(_dir, "bifrost.db");
        Database.OpenFile(path).Dispose();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Database.OpenFile(path).Dispose();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_fails_to_migrate()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bifrost.db");
        await File.WriteAllTextAsync(path, "this is definitely not an sqlite database, just some text padding it out");
        using var factory = Database.OpenFile(path);

        await Assert.ThrowsAnyAsync<Exception>(() => Database.MigrateAsync(factory));
    }
}
