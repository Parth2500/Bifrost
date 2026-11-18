using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.App.Desk;
using Bifrost.App.Service;
using Bifrost.Server.Hosting;

namespace Bifrost.App;

/// <summary>
/// <c>bifrost extension install --from &lt;file&gt; [--sig &lt;file&gt;]</c> and <c>bifrost extension rollback &lt;name&gt;</c>
/// (design §8.3). Host-only: the operating system asks for the password first, then the package goes to Bifröst over the
/// admin socket, which checks its signature before anything is unpacked. <c>--sig</c> defaults to <c>&lt;file&gt;.sig</c>.
/// Works with no internet: the package is a file.
/// </summary>
internal static class ExtensionCommand
{
    public static async Task<int> RunAsync(string[] args, ICommandRunner runner)
    {
        if (args is ["install", "--from", var file]) return await InstallAsync(runner, file, file + ".sig");
        if (args is ["install", "--from", var packageFile, "--sig", var sigFile]) return await InstallAsync(runner, packageFile, sigFile);
        if (args is ["rollback", var name])
        {
            return await PostAsync(runner, $"/api/v1/extensions/{Uri.EscapeDataString(name)}/rollback", null, $"Rolled {name} back. It was restarted.");
        }
        await Console.Error.WriteLineAsync("Usage: bifrost extension install --from <file> [--sig <file>]\n       bifrost extension rollback <name>");
        return 64;
    }

    private static async Task<int> InstallAsync(ICommandRunner runner, string file, string sigFile)
    {
        if (!File.Exists(file) || !File.Exists(sigFile))
        {
            await Console.Error.WriteLineAsync(File.Exists(file) ? $"No signature file at {sigFile}. Give one with --sig." : $"No such file: {file}");
            return 1;
        }
        await using var package = File.OpenRead(file);
        using var content = new StreamContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.Add("X-Bifrost-Signature", Convert.ToBase64String(await File.ReadAllBytesAsync(sigFile)));
        return await PostAsync(runner, "/api/v1/extensions/install", content, null);
    }

    private static async Task<int> PostAsync(ICommandRunner runner, string path, HttpContent? content, string? success)
    {
        if (!await HostGate.RequireAsync(runner))
        {
            await Console.Error.WriteLineAsync("Not allowed: the system password prompt (polkit) was refused or is not installed. `sudo bifrost service install` installs it.");
            return 1;
        }
        using var http = DeskCommand.Client(() => DeskCommand.AdminSocket(Environment.GetEnvironmentVariable, AppPaths.ForCurrentProcess().AdminSocket));
        http.Timeout = TimeSpan.FromMinutes(30); // a package can be large
        try
        {
            using var response = await http.PostAsync(path, content);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Console.Error.WriteLineAsync(problem.TryGetProperty("title", out var t) ? t.GetString() : $"Bifröst refused ({(int)response.StatusCode}).");
                return 1;
            }
            if (success is not null)
            {
                Console.WriteLine(success);
                return 0;
            }
            var done = await response.Content.ReadFromJsonAsync<JsonElement>();
            Console.WriteLine($"Installed {done.GetProperty("name").GetString()} {done.GetProperty("version").GetString()}.");
            Console.WriteLine(done.GetProperty("restart").GetString() == "extension"
                ? "Restart it from the host screen (or `sudo systemctl restart bifrost`) to run the new version. The version before is kept: `bifrost extension rollback <name>`."
                : "Restart Bifröst to start it: sudo systemctl restart bifrost.");
            return 0;
        }
        catch (HttpRequestException)
        {
            await Console.Error.WriteLineAsync("Could not reach Bifröst. Is it running, and are you its admin?");
            return 1;
        }
    }
}
