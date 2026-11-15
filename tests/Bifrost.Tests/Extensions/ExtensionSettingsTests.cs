using Bifrost.Server.Extensions;

namespace Bifrost.Tests.Extensions;

public sealed class ExtensionSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bifrost-settings-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Server.CapturingLogs _logs = new();

    public ExtensionSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IReadOnlyDictionary<string, string> Load(string? json, bool asService = false)
    {
        var path = Path.Combine(_dir, "extensions.json");
        if (json is not null) File.WriteAllText(path, json);
        return ExtensionSettings.Load(path, asService, _logs.CreateLogger("test"));
    }

    [Fact]
    public void A_missing_file_means_no_extensions_and_no_warning()
    {
        Assert.Empty(Load(null));
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void Bad_json_means_no_extensions_and_one_warning()
    {
        Assert.Empty(Load("{ not json"));
        Assert.Single(_logs.Lines);
    }

    [Fact]
    public void Valid_entries_are_kept_and_each_bad_one_is_dropped_with_a_warning()
    {
        var programs = Load("""
            {
              "sleipnir": { "program": "/opt/sleipnir/sleipnir" },
              "saga": { "program": "relative/saga" },
              "evil": { "program": "/opt/evil" },
              "norns": { },
              "files": { "program": "/home/u/files" }
            }
            """);

        Assert.Equal(new Dictionary<string, string> { ["sleipnir"] = "/opt/sleipnir/sleipnir", ["files"] = "/home/u/files" }, programs);
        Assert.Equal(3, _logs.Lines.Count);
        Assert.Contains(_logs.Lines, l => l.Contains("saga", StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, l => l.Contains("evil", StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, l => l.Contains("norns", StringComparison.Ordinal));
    }

    [Fact]
    public void As_the_service_a_program_under_home_is_refused()
    {
        var programs = Load("""{ "sleipnir": { "program": "/home/u/sleipnir" } }""", asService: true);

        Assert.Empty(programs);
        Assert.Contains(_logs.Lines, l => l.Contains("/home", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_are_matched_exactly()
    {
        Assert.Empty(Load("""{ "Sleipnir": { "program": "/opt/s" } }"""));
    }
}
