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
            var queue = Prune(source);
            if (queue is null) _failures[source] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(time.GetUtcNow());
        }
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
