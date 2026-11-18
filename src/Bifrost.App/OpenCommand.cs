using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.App.Desk;
using Bifrost.App.Service;
using Bifrost.Server.Hosting;

namespace Bifrost.App;

/// <summary>
/// <c>bifrost open</c>: opens the host screen in the browser on this machine (ADR 003). Bifröst makes a link that
/// works once for two minutes, but only for the admin socket, where the operating system says who is asking.
/// <c>--print</c> prints the link instead of opening it.
/// </summary>
public static class OpenCommand
{
    public static async Task<int> RunAsync(string[] args, ICommandRunner runner)
    {
        if (args is not ([] or ["--print"]))
        {
            await Console.Error.WriteLineAsync("Usage: bifrost open [--print]");
            return 64;
        }
        using var http = DeskCommand.Client(() => DeskCommand.AdminSocket(Environment.GetEnvironmentVariable, AppPaths.ForCurrentProcess().AdminSocket));
        try
        {
            using var response = await http.PostAsync("/api/v1/host/login-link", null);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                await Console.Error.WriteLineAsync(problem.TryGetProperty("title", out var t) ? t.GetString() : $"Refused ({(int)response.StatusCode}).");
                return 1;
            }
            var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
            if (args is ["--print"])
            {
                Console.WriteLine(url);
                return 0;
            }
            if (await OpenInBrowserAsync(url, runner, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")))
            {
                Console.WriteLine("Opened the host screen in your browser. The link worked once; run this again for a new one.");
                return 0;
            }
            // No private place to hand the browser the link, or no browser: say how to get one, and keep the secret off the screen.
            Console.WriteLine("Could not open a browser from here. Run `bifrost open --print` and open the link on this machine within two minutes.");
            return 1;
        }
        catch (HttpRequestException)
        {
            await Console.Error.WriteLineAsync("Could not reach Bifröst. Is it running, and are you its admin?");
            return 1;
        }
    }

    /// <summary>
    /// The link is a secret that any account could use first, and a program's arguments can be read by every account
    /// on the machine. So the browser is given a file only this account can read, which sends it on to the link, and the
    /// secret is never an argument. The file is removed a few seconds later; it is useless after one use anyway.
    /// </summary>
    /// <returns>False when there is no private folder to put the file in, or the browser could not be started.</returns>
    public static async Task<bool> OpenInBrowserAsync(string url, ICommandRunner runner, string? runtimeDir)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir)) return false;
        var path = Path.Combine(runtimeDir, "bifrost-open-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(path, Launcher(url));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var (exit, _) = await runner.RunAsync("xdg-open", path);
            if (exit == 0) await Task.Delay(TimeSpan.FromSeconds(5)); // time for the browser to read it
            return exit == 0;
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static string Launcher(string url)
    {
        var safe = System.Net.WebUtility.HtmlEncode(url);
        return $"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={safe}\"><title>Bifröst</title><a href=\"{safe}\">Continue to Bifröst</a>\n";
    }
}
