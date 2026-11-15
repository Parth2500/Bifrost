using System.Net.Sockets;
using System.Text.Json;
using Bifrost.Server.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Extensions;

public sealed class SupervisorTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bifrost-sup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly List<ExtensionSupervisor> _started = [];

    private string RuntimeRoot => Path.Combine(_root, "run");
    private string DataRoot => Path.Combine(_root, "state");

    private ExtensionSupervisor Supervisor(string mode = "echo")
    {
        var supervisor = new ExtensionSupervisor("sleipnir", FakeExtension.Program, RuntimeRoot, DataRoot, _time,
            NullLogger.Instance, new Dictionary<string, string> { ["FAKE_MODE"] = mode });
        _started.Add(supervisor);
        return supervisor;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _started) await s.StopAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static async Task Until(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private static async Task<JsonElement> EchoAsync(ExtensionSupervisor supervisor)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(supervisor.SocketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        return JsonDocument.Parse(await http.GetStringAsync("http://localhost/hello")).RootElement;
    }

    [Fact]
    public async Task Starts_the_extension_with_exactly_the_contract_environment()
    {
        Environment.SetEnvironmentVariable("BIFROST_TEST_LEAK", "1");
        try
        {
            var supervisor = Supervisor();
            await supervisor.StartAsync();
            await Until(() => supervisor.State == ExtensionState.Running);

            var env = (await EchoAsync(supervisor)).GetProperty("env");

            Assert.Equal(supervisor.SocketPath, env.GetProperty("BIFROST_SOCKET").GetString());
            Assert.Equal(supervisor.ApiSocketPath, env.GetProperty("BIFROST_API_SOCKET").GetString());
            Assert.Equal(supervisor.DataDir, env.GetProperty("BIFROST_DATA_DIR").GetString());
            Assert.Equal("sleipnir", env.GetProperty("BIFROST_EXTENSION").GetString());
            Assert.Equal("1", env.GetProperty("BIFROST_INTERFACE").GetString());
            Assert.Equal(5, env.EnumerateObject().Count());
            Assert.NotNull(supervisor.Pid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BIFROST_TEST_LEAK", null);
        }
    }

    [Fact]
    public async Task The_folders_are_owner_only()
    {
        var supervisor = Supervisor();
        await supervisor.StartAsync();
        await Until(() => supervisor.State == ExtensionState.Running);

        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(Path.GetDirectoryName(supervisor.SocketPath)!));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(supervisor.DataDir));
        Assert.Equal(Path.Combine(RuntimeRoot, "x", "sleipnir", "extension.sock"), supervisor.SocketPath);
        Assert.Equal(Path.Combine(RuntimeRoot, "x", "sleipnir", "bifrost.sock"), supervisor.ApiSocketPath);
        Assert.Equal(Path.Combine(DataRoot, "x", "sleipnir"), supervisor.DataDir);
    }

    [Fact]
    public async Task A_crashing_extension_is_restarted_with_growing_delays_then_given_up()
    {
        var supervisor = Supervisor("crash");
        await supervisor.StartAsync();

        foreach (var (delay, restarts) in new[] { (1, 1), (2, 2), (4, 3), (8, 4) })
        {
            await Until(() => supervisor.State == ExtensionState.Crashed && supervisor.Restarts == restarts - 1);
            _time.Advance(TimeSpan.FromSeconds(delay) - TimeSpan.FromMilliseconds(1));
            await Task.Delay(100);
            Assert.Equal(restarts - 1, supervisor.Restarts); // not yet
            _time.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => supervisor.Restarts == restarts);
        }

        await Until(() => supervisor.State == ExtensionState.Failed);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(300);
        Assert.Equal(4, supervisor.Restarts);
        Assert.Equal(ExtensionState.Failed, supervisor.State);
    }

    [Fact]
    public async Task An_extension_that_never_opens_its_socket_is_killed_after_20_seconds_and_counted_as_a_crash()
    {
        var supervisor = Supervisor("hang");
        await supervisor.StartAsync();
        await Until(() => supervisor.State == ExtensionState.Starting && supervisor.Pid is not null);
        var pid = supervisor.Pid!.Value;

        _time.Advance(TimeSpan.FromSeconds(19));
        await Task.Delay(300);
        Assert.Equal(ExtensionState.Starting, supervisor.State);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => supervisor.State == ExtensionState.Crashed);
        await Until(() => !File.Exists($"/proc/{pid}/status") || File.ReadAllText($"/proc/{pid}/status").Contains("zombie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ends_a_running_extension_quickly()
    {
        var supervisor = Supervisor();
        await supervisor.StartAsync();
        await Until(() => supervisor.State == ExtensionState.Running);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await supervisor.StopAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stopping took {clock.Elapsed}");
        Assert.Equal(ExtensionState.Stopped, supervisor.State);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(200);
        Assert.Equal(ExtensionState.Stopped, supervisor.State); // no restart after a stop
    }

    [Fact]
    public async Task A_stale_socket_file_does_not_count_as_running()
    {
        var supervisor = Supervisor("hang");
        Directory.CreateDirectory(Path.GetDirectoryName(supervisor.SocketPath)!);
        File.WriteAllText(supervisor.SocketPath, "left over");

        await supervisor.StartAsync();
        await Task.Delay(500);

        Assert.Equal(ExtensionState.Starting, supervisor.State);
    }

    [Fact]
    public async Task A_clean_exit_is_restarted_too()
    {
        var supervisor = Supervisor("exit-after=1");
        await supervisor.StartAsync();
        await Until(() => supervisor.State == ExtensionState.Running);

        await Until(() => supervisor.State == ExtensionState.Crashed);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => supervisor.Restarts == 1);
    }
}
