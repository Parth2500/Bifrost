using System.Text.Json;

namespace Bifrost.Server.Extensions;

/// <summary>
/// Which program each extension runs, from <c>&lt;settings&gt;/extensions.json</c>:
/// <c>{ "sleipnir": { "program": "/opt/sleipnir/sleipnir" } }</c>. Until install from releases
/// (step 5) this file is how an extension is added. It needs root to edit under the service.
/// </summary>
public static partial class ExtensionSettings
{
    public const string FileName = "extensions.json";

    /// <param name="asService">Running as the systemd service, whose unit hides <c>/home</c>.</param>
    public static IReadOnlyDictionary<string, string> Load(string path, bool asService, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var programs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return programs;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            LogUnreadable(log, path, e.Message);
            return programs;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            LogUnreadable(log, path, "the file must hold one JSON object");
            return programs;
        }

        foreach (var entry in root.EnumerateObject())
        {
            if (Refusal(entry, asService) is { } reason)
            {
                LogRefused(log, entry.Name, reason);
                continue;
            }
            programs[entry.Name] = entry.Value.GetProperty("program").GetString()!;
        }
        return programs;
    }

    private static string? Refusal(JsonProperty entry, bool asService)
    {
        if (!ExtensionCatalog.Names.Contains(entry.Name)) return "it is not a known extension";
        if (entry.Value.ValueKind != JsonValueKind.Object
            || !entry.Value.TryGetProperty("program", out var program) || program.ValueKind != JsonValueKind.String)
        {
            return "it has no \"program\"";
        }
        var path = program.GetString()!;
        if (!Path.IsPathFullyQualified(path)) return "its program must be an absolute path";
        if (asService && (path == "/home" || path.StartsWith("/home/", StringComparison.Ordinal)))
        {
            return "its program is under /home, which the service cannot see; put it under /opt";
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring {Path}: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not starting extension {Name}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string name, string reason);
}
