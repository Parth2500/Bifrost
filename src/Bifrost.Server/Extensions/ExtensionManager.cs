namespace Bifrost.Server.Extensions;

/// <summary>Every configured extension's supervisor. Bifröst starts them after its own listener and stops them before it exits.</summary>
public sealed class ExtensionManager : IAsyncDisposable
{
    private readonly Dictionary<string, ExtensionSupervisor> _supervisors;

    /// <param name="programs">Extension name → program, from <see cref="ExtensionSettings"/>.</param>
    /// <param name="extraEnvironment">Per extension, added to its environment; for tests only.</param>
    public ExtensionManager(IReadOnlyDictionary<string, string> programs, string runtimeRoot, string dataRoot, TimeProvider time,
        ILoggerProvider logs, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(logs);
        var log = logs.CreateLogger("Bifrost.Extensions");
        _supervisors = programs
            .Where(p => ExtensionCatalog.Names.Contains(p.Key))
            .ToDictionary(p => p.Key, p => new ExtensionSupervisor(p.Key, p.Value, runtimeRoot, dataRoot, time, log,
                extraEnvironment?.GetValueOrDefault(p.Key)), StringComparer.Ordinal);
    }

    public IReadOnlyList<ExtensionSupervisor> All => _supervisors.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>The supervisor of a configured extension; the name is matched exactly.</summary>
    public ExtensionSupervisor? Get(string name) => _supervisors.GetValueOrDefault(name);

    public async Task StartAllAsync()
    {
        foreach (var supervisor in All) await supervisor.StartAsync();
    }

    public Task StopAllAsync() => Task.WhenAll(All.Select(s => s.StopAsync()));

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
