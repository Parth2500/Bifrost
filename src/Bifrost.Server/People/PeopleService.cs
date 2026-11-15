using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Core.Models;
using Bifrost.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server.People;

/// <summary>
/// People, devices and token authentication (design §5). Every database call goes through one gate:
/// there is one Bifröst per home, so serializing is cheap and makes "keep at least one admin" race-free.
/// Each change and its audit row are saved together, so a refused change leaves no trace.
/// </summary>
public sealed class PeopleService(IDbContextFactory<BifrostDb> dbFactory, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public Task<Principal> EnsureLocalAdminAsync(string adminName, string localToken) => With(async db =>
    {
        var admin = await db.People.Where(p => p.Role == PersonRole.Admin).OrderBy(p => p.CreatedAt).FirstOrDefaultAsync();
        if (admin is null)
        {
            string name;
            try { name = PersonNames.Validate(adminName); }
            catch (PeopleException) { name = "admin"; }
            if (await db.People.AnyAsync(p => p.DisplayName == name)) name = "admin";
            admin = new PersonEntity { Id = Guid.NewGuid(), DisplayName = name, Role = PersonRole.Admin, CreatedAt = Now };
            db.People.Add(admin);
            Audit(db, null, "person.created", admin.Id, null);
        }

        var device = await db.Devices.FirstOrDefaultAsync(d => d.IsLocal);
        if (device is null)
        {
            device = new DeviceEntity
            {
                Id = Guid.NewGuid(), PersonId = admin.Id, Name = "This computer", IsLocal = true, Kind = DeviceKind.App,
                Platform = RuntimeInformation.OSDescription, CreatedAt = Now,
            };
            db.Devices.Add(device);
            Audit(db, null, "device.issued", device.Id, null);
        }
        device.TokenHash = HashToken(localToken); // the token file may have been regenerated since the last start
        device.RevokedAt = null;
        if (!await db.People.AnyAsync(p => p.Id == device.PersonId && p.Role == PersonRole.Admin)) device.PersonId = admin.Id;

        await db.SaveChangesAsync();
        var owner = await db.People.SingleAsync(p => p.Id == device.PersonId);
        return ToPrincipal(owner, device);
    });

    public Task<Principal?> AuthenticateAsync(string token) => With<Principal?>(async db =>
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAt == null);
        if (device is null) return null;
        var person = await db.People.SingleAsync(p => p.Id == device.PersonId);

        var now = Now;
        if (device.LastSeenAt is null || now - device.LastSeenAt >= LastSeenInterval)
        {
            device.LastSeenAt = now;
            await db.SaveChangesAsync();
        }
        return ToPrincipal(person, device);
    });

    /// <summary>Creates a device and its token. Pairing (step 3) will be the caller; until then nobody is the actor.</summary>
    public Task<IssuedDevice> IssueDeviceAsync(Guid personId, string name, string platform, DeviceKind kind) => With(async db =>
    {
        if (!await db.People.AnyAsync(p => p.Id == personId)) throw new PeopleException("person_not_found");
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(), PersonId = personId, Name = name, Platform = platform, Kind = kind,
            TokenHash = HashToken(token), CreatedAt = Now,
        };
        db.Devices.Add(device);
        Audit(db, null, "device.issued", device.Id, null);
        await db.SaveChangesAsync();
        return new IssuedDevice(device, token);
    });

    public Task<bool> RevokeDeviceAsync(Principal actor, Guid deviceId) => With(async db =>
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device is null) return false;
        if (device.RevokedAt is null)
        {
            device.RevokedAt = Now;
            Audit(db, actor, "device.revoked", device.Id, null);
            await db.SaveChangesAsync();
        }
        return true;
    });

    public Task<PersonEntity> CreatePersonAsync(Principal actor, string displayName, PersonRole role) => With(async db =>
    {
        var name = PersonNames.Validate(displayName);
        if (await db.People.AnyAsync(p => p.DisplayName == name)) throw new PeopleException("name_taken");
        var person = new PersonEntity { Id = Guid.NewGuid(), DisplayName = name, Role = role, CreatedAt = Now };
        db.People.Add(person);
        Audit(db, actor, "person.created", person.Id, StoredWords.Role(role));
        await db.SaveChangesAsync();
        return person;
    });

    public Task<PersonEntity> UpdatePersonAsync(Principal actor, Guid id, string? displayName, PersonRole? role) => With(async db =>
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id) ?? throw new PeopleException("person_not_found");
        if (displayName is not null)
        {
            var name = PersonNames.Validate(displayName);
            if (await db.People.AnyAsync(p => p.Id != id && p.DisplayName == name)) throw new PeopleException("name_taken");
            if (name != person.DisplayName)
            {
                Audit(db, actor, "person.renamed", id, $"{person.DisplayName}->{name}");
                person.DisplayName = name;
            }
        }
        if (role is { } newRole && newRole != person.Role)
        {
            if (person.Role == PersonRole.Admin && await AdminCount(db) <= 1) throw new PeopleException("last_admin");
            Audit(db, actor, "person.role_changed", id, $"{StoredWords.Role(person.Role)}->{StoredWords.Role(newRole)}");
            person.Role = newRole;
        }
        await db.SaveChangesAsync();
        return person;
    });

    public Task DeletePersonAsync(Principal actor, Guid id) => With(async db =>
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id) ?? throw new PeopleException("person_not_found");
        if (person.Role == PersonRole.Admin && await AdminCount(db) <= 1) throw new PeopleException("last_admin");
        db.People.Remove(person); // devices cascade
        Audit(db, actor, "person.deleted", id, person.DisplayName);
        await db.SaveChangesAsync();
        return true;
    });

    public Task<PersonEntity?> GetPersonAsync(Guid id) => With(db => db.People.FirstOrDefaultAsync(p => p.Id == id));

    public Task<IReadOnlyList<PersonEntity>> ListPeopleAsync() =>
        With<IReadOnlyList<PersonEntity>>(async db => await db.People.OrderBy(p => p.CreatedAt).ThenBy(p => p.DisplayName).ToListAsync());

    public Task<DeviceEntity?> GetDeviceAsync(Guid id) => With(db => db.Devices.FirstOrDefaultAsync(d => d.Id == id));

    public Task<IReadOnlyList<DeviceEntity>> ListDevicesAsync(Guid? personId) => With<IReadOnlyList<DeviceEntity>>(async db =>
        await db.Devices.Where(d => personId == null || d.PersonId == personId).OrderBy(d => d.CreatedAt).ToListAsync());

    public void Dispose() => _gate.Dispose();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static Principal ToPrincipal(PersonEntity person, DeviceEntity device) =>
        new(person.Id, person.DisplayName, person.Role, device.Id, device.Name, device.Kind, device.IsLocal);

    private void Audit(BifrostDb db, Principal? actor, string kind, Guid subject, string? detail) =>
        db.Audit.Add(new AuditEntity
        {
            At = Now, Kind = kind, ActorPersonId = actor?.PersonId, ActorDeviceId = actor?.DeviceId, SubjectId = subject, Detail = detail,
        });

    private static Task<int> AdminCount(BifrostDb db) => db.People.CountAsync(p => p.Role == PersonRole.Admin);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<T> With<T>(Func<BifrostDb, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await work(db);
        }
        finally
        {
            _gate.Release();
        }
    }
}
