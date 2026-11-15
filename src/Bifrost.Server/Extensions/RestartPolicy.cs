namespace Bifrost.Server.Extensions;

/// <summary>
/// When to start an extension again after it exits (design §8.4): a growing delay, and after
/// repeated crashes, no more tries. A run longer than the window counts as healthy and clears the history.
/// </summary>
public sealed class RestartPolicy(int maxCrashes = 5)
{
    private static readonly TimeSpan[] Delays = new[] { 1, 2, 4, 8, 16, 32, 60 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly List<DateTimeOffset> _exits = [];

    /// <returns>How long to wait before the next start, or null to give up.</returns>
    public TimeSpan? OnExit(DateTimeOffset startedAt, DateTimeOffset exitedAt)
    {
        if (exitedAt - startedAt >= Window) _exits.Clear();
        _exits.RemoveAll(at => exitedAt - at >= Window);
        _exits.Add(exitedAt);
        return _exits.Count >= maxCrashes ? null : Delays[Math.Min(_exits.Count, Delays.Length) - 1];
    }

    public void Reset() => _exits.Clear();
}
