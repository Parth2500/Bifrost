using System.Security.Cryptography;
using System.Text;
using Bifrost.Core.Models;

namespace Bifrost.Server.People;

/// <summary>
/// Pairing path B (design §7.2): a device with no camera asks to join, both sides show a 6-digit code
/// derived from the fingerprint the device really saw and both nonces, and an admin who sees the same
/// code approves. A device in the middle shows a different certificate, so its code differs.
/// </summary>
public sealed class JoinService(PeopleService people, byte[] fingerprint, TimeProvider time)
{
    public const int MaxPending = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public sealed record Request(Guid Id, string Name, string Platform, string Code, DateTimeOffset Expires);

    private sealed class Entry(Request view, byte[] deviceNonceHash)
    {
        public Request View { get; } = view;
        public byte[] DeviceNonceHash { get; } = deviceNonceHash;
        public Guid? PersonId { get; set; }
        public bool Rejected { get; set; }
    }

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <returns>The request and the server's nonce; null when 5 requests already wait.</returns>
    public (Guid Id, string ServerNonce)? Start(string name, string platform, string deviceNonce)
    {
        var serverNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var id = Guid.NewGuid();
        lock (_gate)
        {
            Purge();
            if (_entries.Count >= MaxPending) return null;
            var code = Code(fingerprint, deviceNonce, serverNonce);
            _entries[id] = new Entry(new Request(id, name, platform, code, time.GetUtcNow() + Lifetime), Hash(deviceNonce));
        }
        return (id, serverNonce);
    }

    /// <summary>What the device computes from the fingerprint it saw, and the host shows the admin.</summary>
    public static string Code(byte[] fingerprint, string deviceNonce, string serverNonce)
    {
        var hash = SHA256.HashData([.. fingerprint, .. Encoding.UTF8.GetBytes($"|{deviceNonce}|{serverNonce}")]);
        return (BitConverter.ToUInt32(hash, 0) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<Request> Pending()
    {
        lock (_gate)
        {
            Purge();
            return [.. _entries.Values.Where(e => e.PersonId is null && !e.Rejected).Select(e => e.View)];
        }
    }

    public bool Approve(Guid id, Guid personId)
    {
        lock (_gate)
        {
            Purge();
            if (!_entries.TryGetValue(id, out var e) || e.PersonId is not null || e.Rejected) return false;
            e.PersonId = personId;
            return true;
        }
    }

    public bool Reject(Guid id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e) || e.PersonId is not null) return false;
            e.Rejected = true;
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
        var device = await people.IssueDeviceAsync(entry.PersonId.Value, entry.View.Name, entry.View.Platform, DeviceKind.App);
        return (Outcome.Approved, device);
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var id in _entries.Where(e => e.Value.View.Expires <= now).Select(e => e.Key).ToList()) _entries.Remove(id);
    }

    private static byte[] Hash(string nonce) => SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
}
