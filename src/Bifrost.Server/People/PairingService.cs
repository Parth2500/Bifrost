using System.Security.Cryptography;
using System.Text;
using Bifrost.Core.Models;

namespace Bifrost.Server.People;

/// <summary>
/// Pairing path A (design §7.1): an admin makes a single-use invitation for a person; a device that
/// presents its secret gets a device token. Invitations live in memory for 10 minutes and are kept
/// only as hashes, so neither a restart nor a memory dump leaves a usable secret behind.
/// </summary>
public sealed class PairingService(PeopleService people, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxOpen = 100;

    private readonly Dictionary<string, (Guid PersonId, DateTimeOffset Expires)> _open = [];
    private readonly Lock _gate = new();

    /// <returns>The secret (shown once) and when it expires; null when too many invitations are open.</returns>
    public (string Secret, DateTimeOffset Expires)? Invite(Guid personId)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        var expires = time.GetUtcNow() + Lifetime;
        lock (_gate)
        {
            Purge();
            if (_open.Count >= MaxOpen) return null;
            _open[Hash(secret)] = (personId, expires);
        }
        return (secret, expires);
    }

    /// <returns>The new device, or null when the secret is unknown, used or expired.</returns>
    public async Task<IssuedDevice?> RedeemAsync(string secret, string name, string platform)
    {
        (Guid PersonId, DateTimeOffset Expires) invitation;
        lock (_gate)
        {
            Purge();
            // Removed before anything else: a secret works once, even for two requests at the same moment.
            if (!_open.Remove(Hash(secret), out invitation)) return null;
        }
        return await people.IssueDeviceAsync(invitation.PersonId, name, platform, DeviceKind.App);
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var key in _open.Where(i => i.Value.Expires <= now).Select(i => i.Key).ToList()) _open.Remove(key);
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
