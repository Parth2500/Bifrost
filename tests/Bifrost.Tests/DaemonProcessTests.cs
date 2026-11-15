using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bifrost.Tests;

public class DaemonProcessTests
{
    private const int SIGKILL = 9, SIGTERM = 15;

    private static HttpClient ClientFor(int port, string? token = null)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<HttpStatusCode> MeAsync(int port, string token)
    {
        using var http = ClientFor(port, token);
        return (await http.GetAsync("/api/v1/me")).StatusCode;
    }

    [Fact]
    public async Task Starts_answers_and_stops_on_SIGTERM()
    {
        using var machine = new BifrostMachine();
        using var daemon = machine.Start("daemon");

        var info = await machine.WaitForDaemonAsync(daemon);

        Assert.Equal(daemon.Id, info.Pid);
        using var admin = ClientFor(info.Port, machine.Token);
        var me = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/me")).RootElement;
        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.Equal(Environment.UserName, me.GetProperty("personName").GetString());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(machine.TokenPath));

        daemon.Signal(SIGTERM);

        Assert.Equal(0, await daemon.WaitForExitAsync(10));
        Assert.False(File.Exists(Path.Combine(machine.RuntimeDir, "daemon.json")));
    }

    [Fact]
    public async Task The_shutdown_endpoint_stops_the_daemon_cleanly()
    {
        using var machine = new BifrostMachine();
        using var daemon = machine.Start("daemon");
        var info = await machine.WaitForDaemonAsync(daemon);

        using var admin = ClientFor(info.Port, machine.Token);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/server/shutdown", null)).StatusCode);

        Assert.Equal(0, await daemon.WaitForExitAsync(10));
    }

    [Fact]
    public async Task A_second_daemon_exits_with_1()
    {
        using var machine = new BifrostMachine();
        using var first = machine.Start("daemon");
        await machine.WaitForDaemonAsync(first);

        using var second = machine.Start("daemon");

        Assert.Equal(1, await second.WaitForExitAsync());
        Assert.Contains("already running", second.Output, StringComparison.Ordinal);
        Assert.False(first.HasExited);
    }

    [Fact]
    public async Task After_a_crash_a_new_daemon_starts_despite_the_leftover_files()
    {
        using var machine = new BifrostMachine();
        using var crashed = machine.Start("daemon");
        await machine.WaitForDaemonAsync(crashed);
        crashed.Signal(SIGKILL);
        await crashed.WaitForExitAsync(10);

        using var next = machine.Start("daemon");

        Assert.Equal(next.Id, (await machine.WaitForDaemonAsync(next)).Pid);
    }

    [Fact]
    public async Task A_deleted_token_file_is_replaced_and_the_old_token_stops_working()
    {
        using var machine = new BifrostMachine();
        string old;
        using (var first = machine.Start("daemon"))
        {
            await machine.WaitForDaemonAsync(first);
            old = machine.Token;
            first.Signal(SIGTERM);
            Assert.Equal(0, await first.WaitForExitAsync(10));
        }
        File.Delete(machine.TokenPath);

        using var second = machine.Start("daemon");
        var info = await machine.WaitForDaemonAsync(second);

        Assert.NotEqual(old, machine.Token);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(info.Port, machine.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(info.Port, old));
    }

    [Fact]
    public async Task A_widened_database_file_is_made_owner_only_again()
    {
        using var machine = new BifrostMachine();
        using (var first = machine.Start("daemon"))
        {
            await machine.WaitForDaemonAsync(first);
            first.Signal(SIGTERM);
            Assert.Equal(0, await first.WaitForExitAsync(10));
        }
        File.SetUnixFileMode(machine.DatabasePath, (UnixFileMode)0b110_100_100); // 0644

        using var second = machine.Start("daemon");
        await machine.WaitForDaemonAsync(second);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(machine.DatabasePath));
    }

    [Fact]
    public async Task An_unwritable_data_folder_exits_with_74_and_says_why()
    {
        using var machine = new BifrostMachine();
        Directory.CreateDirectory(machine.DataDir);
        File.SetUnixFileMode(machine.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // 0500

        using var daemon = machine.Start("daemon");

        Assert.Equal(74, await daemon.WaitForExitAsync());
        Assert.Contains("Bifröst could not start", daemon.Output, StringComparison.Ordinal);
        Assert.Contains(machine.DataDir, daemon.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", daemon.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        using var machine = new BifrostMachine();
        using var process = machine.Start("frobnicate");

        Assert.Equal(64, await process.WaitForExitAsync());
        Assert.Contains("daemon", process.Output, StringComparison.Ordinal);
    }
}
