using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.App.Desk;
using Bifrost.Server.Hosting;
using QRCoder;

namespace Bifrost.App;

/// <summary>
/// <c>bifrost invite [person]</c>: an invitation link for a device, printed with a QR code to scan (design §7.1).
/// Without a name it is for the admin's own person. Needs the home network listener on.
/// </summary>
public static class InviteCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 1)
        {
            await Console.Error.WriteLineAsync("Usage: bifrost invite [person name]");
            return 64;
        }
        using var http = DeskCommand.Client(() => DeskCommand.AdminSocket(Environment.GetEnvironmentVariable, AppPaths.ForCurrentProcess().AdminSocket));
        try
        {
            var me = await http.GetFromJsonAsync<JsonElement>("/api/v1/me");
            var personId = me.GetProperty("personId").GetGuid();
            if (args.Length == 1)
            {
                var people = await http.GetFromJsonAsync<JsonElement>("/api/v1/people");
                var match = people.EnumerateArray().Where(p => string.Equals(p.GetProperty("displayName").GetString(), args[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count != 1)
                {
                    await Console.Error.WriteLineAsync(match.Count == 0 ? $"No person named '{args[0]}'." : $"More than one person is named '{args[0]}'.");
                    return 1;
                }
                personId = match[0].GetProperty("id").GetGuid();
            }
            using var response = await http.PostAsJsonAsync("/api/v1/invitations", new { personId });
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Console.Error.WriteLineAsync(problem.TryGetProperty("title", out var t) ? t.GetString() : $"Refused ({(int)response.StatusCode}).");
                return 1;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var link = body.GetProperty("link").GetString()!;
            Console.WriteLine(QrText(link));
            Console.WriteLine(link);
            Console.WriteLine($"Scan it, or paste it into the Bifröst app. It works once and expires {body.GetProperty("expiresAt").GetDateTimeOffset().ToLocalTime():HH:mm}.");
            return 0;
        }
        catch (HttpRequestException)
        {
            await Console.Error.WriteLineAsync("Could not reach Bifröst. Is it running, and are you its admin?");
            return 1;
        }
    }

    /// <summary>Light blocks on a dark terminal, with a quiet zone: scanners read it either way round.</summary>
    public static string QrText(string text)
    {
        using var data = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        return new AsciiQRCode(data).GetGraphic(1, "  ", "██", drawQuietZones: true);
    }
}
