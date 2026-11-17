using System.Net.Http.Json;
using Bifrost.App.Desk;
using Bifrost.App.Service;
using Bifrost.Server.Hosting;

namespace Bifrost.App;

/// <summary><c>bifrost identity rotate</c>: a new home key. Host-only: the operating system asks for the password first.</summary>
internal static class IdentityCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["rotate"])
        {
            await Console.Error.WriteLineAsync("Usage: bifrost identity rotate");
            return 64;
        }
        if (!await HostGate.RequireAsync(new ProcessCommandRunner()))
        {
            await Console.Error.WriteLineAsync("Not allowed: the system password prompt (polkit) was refused or is not installed. `sudo bifrost service install` installs its policy.");
            return 1;
        }
        using var http = DeskCommand.Client(() => DeskCommand.AdminSocket(Environment.GetEnvironmentVariable, AppPaths.ForCurrentProcess().AdminSocket));
        using var response = await http.PostAsync("/api/v1/identity/rotate", null);
        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync($"Bifröst refused ({(int)response.StatusCode}). Is it running, and are you its admin?");
            return 1;
        }
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Console.WriteLine($"New home key {body.GetProperty("fingerprint").GetString()}.");
        Console.WriteLine("Restart Bifröst to use it (sudo systemctl restart bifrost). Every device must pair again.");
        return 0;
    }
}
