using System.Text.Json;
using Bifrost.Server.Security;

namespace Bifrost.Server.Hosting;

/// <summary>
/// The operating-system account that is the host admin (design §5), from <c>&lt;settings&gt;/admin.json</c>:
/// <c>{ "uid": 1000, "name": "parth" }</c>, written by <c>bifrost service install</c>. Without the file
/// (a developer running the daemon by hand) the admin is the account the daemon runs as.
/// </summary>
public sealed record AdminSettings(uint Uid, string Name)
{
    public const string FileName = "admin.json";

    public static AdminSettings Load(string settingsDir)
    {
        var path = Path.Combine(settingsDir, FileName);
        if (!File.Exists(path)) return new AdminSettings(PeerCredentials.CurrentUid(), Environment.UserName);

        // A file that is there but unreadable must stop the start: guessing an admin is worse than having none.
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("uid", out var uid) && uid.TryGetUInt32(out var id) && id != 0
                && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } text)
            {
                return new AdminSettings(id, text);
            }
        }
        catch (JsonException)
        {
            // Reported below.
        }
        throw new IOException($"{path} must hold {{ \"uid\": <number, not 0>, \"name\": \"<account>\" }}.");
    }
}
