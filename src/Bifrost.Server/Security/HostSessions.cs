using System.Security.Cryptography;

namespace Bifrost.Server.Security;

/// <summary>
/// How a browser on the host proves it is the admin (ADR 003). The admin socket, where the kernel names the
/// calling account, hands out a link that works once and for two minutes; opening it in a browser on this
/// machine trades it for a session. Secrets are 256-bit and only their SHA-256 is kept (D3), and everything
/// lives in memory: a restart ends every link and every session.
/// </summary>
public sealed class HostSessions(TimeProvider time)
{
    public static readonly TimeSpan LinkLife = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IdleLife = TimeSpan.FromHours(1);
    public static readonly TimeSpan AbsoluteLife = TimeSpan.FromHours(8);
    private const int MaxOpenLinks = 5;
    private const int MaxSessions = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _links = []; // hash → expires
    private readonly Dictionary<string, Session> _sessions = []; // hash → session

    private sealed record Session(DateTimeOffset Born, DateTimeOffset Seen);

    /// <returns>The secret to put in the link, or null when too many links are already open.</returns>
    public string? NewLink(out DateTimeOffset expires)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            expires = now + LinkLife;
            if (_links.Count >= MaxOpenLinks) return null;
            var secret = NewSecret();
            _links[Hash(secret)] = expires;
            return secret;
        }
    }

    /// <summary>Spends a link. The same secret never works twice.</summary>
    /// <returns>A new session secret, or null when the link is unknown, used or expired.</returns>
    public string? Redeem(string secret)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (!_links.Remove(Hash(secret))) return null;
            if (_sessions.Count >= MaxSessions) _sessions.Remove(_sessions.MinBy(s => s.Value.Seen).Key); // the least recent makes room
            var session = NewSecret();
            _sessions[Hash(session)] = new Session(now, now);
            return session;
        }
    }

    /// <summary>True while the session is alive; each use keeps it alive for another idle period.</summary>
    public bool Validate(string session)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            var key = Hash(session);
            if (!_sessions.TryGetValue(key, out var found)) return false;
            _sessions[key] = found with { Seen = now };
            return true;
        }
    }

    public void End(string session)
    {
        lock (_gate) _sessions.Remove(Hash(session));
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var gone in _links.Where(l => l.Value <= now).Select(l => l.Key).ToList()) _links.Remove(gone);
        foreach (var gone in _sessions.Where(s => now - s.Value.Seen >= IdleLife || now - s.Value.Born >= AbsoluteLife).Select(s => s.Key).ToList())
        {
            _sessions.Remove(gone);
        }
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));
}
