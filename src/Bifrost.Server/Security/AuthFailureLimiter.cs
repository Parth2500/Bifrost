namespace Bifrost.Server.Security;

/// <summary>Counts failed token checks per source and blocks a source that fails too often within a window.</summary>
public sealed class AuthFailureLimiter(int maxFailures, TimeSpan window, TimeProvider time)
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = [];
    private readonly Lock _gate = new();

    public bool IsBlocked(string source)
    {
        lock (_gate)
        {
            return Prune(source)?.Count >= maxFailures;
        }
    }

    public void RecordFailure(string source)
    {
        lock (_gate)
        {
            if (_failures.Count >= MaxSources) Sweep();
            var queue = Prune(source);
            // shortcut: past the cap a new source is not tracked; the 20 failures/minute budget then
            // cannot block it, but memory stays bounded. Upgrade with a global LAN failure budget.
            if (queue is null && _failures.Count >= MaxSources) return;
            if (queue is null) _failures[source] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(time.GetUtcNow());
        }
    }

    private const int MaxSources = 10_000;

    private void Sweep()
    {
        foreach (var source in _failures.Keys.ToList()) Prune(source);
    }

    private Queue<DateTimeOffset>? Prune(string source)
    {
        if (!_failures.TryGetValue(source, out var queue)) return null;
        var cutoff = time.GetUtcNow() - window;
        while (queue.Count > 0 && queue.Peek() <= cutoff) queue.Dequeue();
        if (queue.Count == 0)
        {
            _failures.Remove(source);
            return null;
        }
        return queue;
    }
}
