using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bifrost.Server.Extensions;

/// <summary>A folder an extension asks Bifröst for, and whether it writes to it.</summary>
public sealed record ReleaseFolder(string Name, bool Write);

/// <summary>What a release says about itself (design §8.2): <c>manifest.json</c> at the root of the package.</summary>
public sealed partial record ReleaseManifest(
    string Name, string Version, int InterfaceMin, int InterfaceMax, IReadOnlyDictionary<string, string> Programs,
    IReadOnlyList<string> Emits, IReadOnlyList<string> Wants, bool AcceptsBrowserFallback, IReadOnlyList<string> FallbackRoutes,
    IReadOnlyList<ReleaseFolder> Folders)
{
    /// <summary>The extension interface this Bifröst speaks.</summary>
    public const int InterfaceVersion = 1;

    public sealed class InvalidManifestException(string message) : Exception(message);

    /// <summary>The platform key a program is listed under, such as <c>linux-x64</c>.</summary>
    public static string Platform =>
        "linux-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public string ProgramHere => Programs.TryGetValue(Platform, out var program) ? program
        : throw new InvalidManifestException($"This release has no program for {Platform}.");

    /// <exception cref="InvalidManifestException">Anything missing, malformed, unknown, or for a different interface.</exception>
    public static ReleaseManifest Parse(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new InvalidManifestException("manifest.json is not valid JSON: " + e.Message);
        }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidManifestException("manifest.json must hold one object.");

        var name = Text(root, "name");
        if (!ExtensionCatalog.Names.Contains(name)) throw new InvalidManifestException($"'{name}' is not a known extension.");
        var version = Text(root, "version");
        if (!VersionPattern().IsMatch(version)) throw new InvalidManifestException("The version must look like 1.2.3.");

        var (min, max) = (1, 1);
        if (root.TryGetProperty("interface", out var range))
        {
            min = Number(range, "min");
            max = Number(range, "max");
        }
        if (min > max || InterfaceVersion < min || InterfaceVersion > max)
        {
            throw new InvalidManifestException($"This release needs interface {min} to {max}; this Bifröst speaks {InterfaceVersion}.");
        }

        var programs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("program", out var table) || table.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidManifestException("The manifest needs a \"program\" for each platform.");
        }
        foreach (var entry in table.EnumerateObject())
        {
            var path = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()! : "";
            if (!SafeRelative(path)) throw new InvalidManifestException($"The program for {entry.Name} must be a path inside the package.");
            programs[entry.Name] = path;
        }

        var folders = new List<ReleaseFolder>();
        if (root.TryGetProperty("folders", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in list.EnumerateArray())
            {
                folders.Add(new ReleaseFolder(Text(folder, "name"), folder.TryGetProperty("write", out var w) && w.ValueKind == JsonValueKind.True));
            }
        }
        var fallback = root.TryGetProperty("browser-fallback", out var bf) && bf.ValueKind == JsonValueKind.Object ? bf : (JsonElement?)null;
        return new ReleaseManifest(name, version, min, max, programs, Strings(root, "emits"), Strings(root, "wants"),
            fallback is { } f && f.TryGetProperty("accepts", out var a) && a.ValueKind == JsonValueKind.True,
            fallback is { } g ? Strings(g, "routes") : [], folders);
    }

    /// <summary>Compares two <c>1.2.3</c> versions numerically.</summary>
    public static int Compare(string a, string b) => System.Version.Parse(a).CompareTo(System.Version.Parse(b));

    internal static bool SafeRelative(string path) =>
        path.Length is > 0 and < 200 && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains('\0')
        && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static string Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and < 100 } s
            ? s : throw new InvalidManifestException($"The manifest needs a \"{key}\".");

    private static int Number(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) && n >= 0
            ? n : throw new InvalidManifestException($"The interface range needs a whole number \"{key}\".");

    private static List<string> Strings(JsonElement element, string key) =>
        element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).Take(100).ToList() : [];

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionPattern();
}
