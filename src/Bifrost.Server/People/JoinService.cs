using System.Security.Cryptography;
using System.Text;
using Bifrost.Core.Models;

namespace Bifrost.Server.People;

/// <summary>
/// Pairing path B (design §7.2): a device with no camera asks to join, both sides show an 8-digit code
/// derived from the fingerprint the device really saw and both nonces, and an admin who sees the same
/// code approves. A device in the middle shows a different certificate, so its code differs.
/// The device commits to its nonce (a hash) before it sees the server's, and reveals it after; so
/// neither side can pick a nonce to make a middleman's two codes match.
/// </summary>
public sealed class JoinService(PeopleService people, byte[] fingerprint, TimeProvider time)
{
    public const int MaxPending = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>A real device reveals its nonce at once; one that does not frees its place quickly.</summary>
    public static readonly TimeSpan RevealWithin = TimeSpan.FromSeconds(30);

    public sealed record Request(Guid Id, string Name, string Platform, string Code, DateTimeOffset Expires);

    private sealed class Entry(string name, string platform, DateTimeOffset expires, byte[] commit, string serverNonce, string source)
    {
        public string Source { get; } = source;
        public string Name { get; } = name;
        public string Platform { get; } = platform;
        public DateTimeOffset Expires { get; set; } = expires;
        public byte[] DeviceNonceHash { get; } = commit;
        public string ServerNonce { get; } = serverNonce;
        public Request? View { get; set; } // set when the nonce is revealed
        public Guid? PersonId { get; set; }
        public bool Rejected { get; set; }
    }

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <returns>The request and the server's nonce; null when 5 requests already wait.</returns>
    public (Guid Id, string ServerNonce)? Start(string name, string platform, byte[] commit, string source)
    {
        var serverNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var id = Guid.NewGuid();
        lock (_gate)
        {
            Purge();
            // One waiting request per source, so one machine cannot fill the five places.
            if (_entries.Count >= MaxPending || _entries.Values.Any(e => e.Source == source)) return null;
            _entries[id] = new Entry(name, platform, time.GetUtcNow() + RevealWithin, commit, serverNonce, source);
        }
        return (id, serverNonce);
    }

    /// <summary>The device shows its nonce; it must match what it committed to. Only now is there a code to compare.</summary>
    public bool Reveal(Guid id, string deviceNonce)
    {
        lock (_gate)
        {
            Purge();
            if (!_entries.TryGetValue(id, out var e) || e.View is not null
                || !CryptographicOperations.FixedTimeEquals(e.DeviceNonceHash, Hash(deviceNonce))) return false;
            e.Expires = time.GetUtcNow() + Lifetime;
            e.View = new Request(id, e.Name, e.Platform, Code(fingerprint, deviceNonce, e.ServerNonce), e.Expires);
            return true;
        }
    }

    /// <summary>What the device computes from the fingerprint it saw, and the host shows the admin.</summary>
    public static string Code(byte[] fingerprint, string deviceNonce, string serverNonce)
    {
        var hash = SHA256.HashData([.. fingerprint, .. Encoding.UTF8.GetBytes($"|{deviceNonce}|{serverNonce}")]);
        return (BitConverter.ToUInt32(hash, 0) % 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Request> Pending()
    {
        lock (_gate)
        {
            Purge();
            return [.. _entries.Values.Where(e => e.PersonId is null && !e.Rejected && e.View is not null).Select(e => e.View!)];
        }
    }

    public bool Approve(Guid id, Guid personId)
    {
        lock (_gate)
        {
            Purge();
            if (!_entries.TryGetValue(id, out var e) || e.View is null || e.PersonId is not null || e.Rejected) return false;
            e.PersonId = personId;
            return true;
        }
    }

    public bool Reject(Guid id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e) || e.PersonId is not null) return false;
            _entries.Remove(id); // gone at once: a refusal frees its place; the device's next ask is "unknown"
            return true;
        }
    }

    public enum Outcome { Unknown, Waiting, Rejected, Approved }

    /// <summary>The device asks. An approval hands the token over once; the request is gone after.</summary>
    public async Task<(Outcome Outcome, IssuedDevice? Device)> CollectAsync(Guid id, string deviceNonce)
    {
        Entry entry;
        lock (_gate)
        {
            Purge();
            if (!_entries.TryGetValue(id, out entry!) || !CryptographicOperations.FixedTimeEquals(entry.DeviceNonceHash, Hash(deviceNonce)))
            {
                return (Outcome.Unknown, null);
            }
            if (entry.Rejected) { _entries.Remove(id); return (Outcome.Rejected, null); }
            if (entry.PersonId is null) return (Outcome.Waiting, null);
            _entries.Remove(id);
        }
        var device = await people.IssueDeviceAsync(entry.PersonId.Value, entry.Name, entry.Platform, DeviceKind.App);
        return (Outcome.Approved, device);
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var id in _entries.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) _entries.Remove(id);
    }

    private static byte[] Hash(string nonce) => SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
}
