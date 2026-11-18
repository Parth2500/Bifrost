using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bifrost.Core.Models;
using Bifrost.Server.Extensions;
using Bifrost.Server.Hosting;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Server;

/// <summary>A real host on an ephemeral loopback port, with an in-memory store and a fake clock.</summary>
public sealed class HostFixture : IAsyncDisposable
{
    public required BifrostHost Host { get; init; }
    public required HttpClient Http { get; init; }
    public required string Token { get; init; }
    public required PeopleService People { get; init; }
    public required BifrostDbFactory Database { get; init; }
    public required Principal Admin { get; init; }
    public required FakeTimeProvider Time { get; init; }
    public required CapturingLogs Logs { get; init; }
    public required Bifrost.Server.Questions.QuestionService Questions { get; init; }

    /// <summary>A client on the admin socket: no token, the operating system says who is calling.</summary>
    public required HttpClient AdminHttp { get; init; }
    public required string AdminSocket { get; init; }
    public ExtensionManager? Extensions { get; init; }
    public string? Root { get; init; }
    public int Port => Host.Port;

    /// <param name="extensions">Fake extensions to run: name → <c>FAKE_MODE</c>.</param>
    /// <param name="adminUid">The account the admin socket accepts; this process's own when null.</param>
    public static async Task<HostFixture> StartAsync(int port = 0, IReadOnlyDictionary<string, string>? extensions = null, uint? adminUid = null, LanOptions? lan = null, ExtensionInstaller? installer = null)
    {
        var database = Bifrost.Server.Persistence.Database.OpenMemory();
        await Bifrost.Server.Persistence.Database.MigrateAsync(database);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var people = new PeopleService(database, time);
        var token = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..43];
        var admin = await people.EnsureLocalAdminAsync("admin", token);
        var logs = new CapturingLogs();

        ExtensionManager? manager = null;
        var root = Path.Combine(Path.GetTempPath(), "bifrost-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var adminSocket = Path.Combine(root, "bifrost-admin", "admin.sock");
        if (extensions is not null)
        {
            manager = new ExtensionManager(
                extensions.Keys.ToDictionary(k => k, _ => Bifrost.Tests.Extensions.FakeExtension.Program),
                Path.Combine(root, "run"), Path.Combine(root, "state"), time, logs,
                extensions.ToDictionary(e => e.Key, e => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["FAKE_MODE"] = e.Value }));
            await manager.StartAllAsync();
        }

        var options = new BifrostHostOptions(port, new AdminSocketOptions(adminSocket, adminUid ?? Bifrost.Server.Security.PeerCredentials.CurrentUid()), lan);
        var questions = new Bifrost.Server.Questions.QuestionService(people, time);
        var host = BifrostHost.Create(options, people, time, logs, manager, questions, installer);
        await host.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        return new HostFixture
        {
            Host = host, Http = http, Token = token, People = people, Admin = admin, Database = database, Time = time, Logs = logs,
            Extensions = manager, Root = root, AdminHttp = UnixHttp.Client(adminSocket), AdminSocket = adminSocket, Questions = questions,
        };
    }

    /// <summary>Waits (in real time) until every configured extension is running.</summary>
    public async Task WaitForExtensionsAsync(int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Extensions!.All.Any(e => e.State != ExtensionState.Running))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("extensions did not start");
            await Task.Delay(20);
        }
    }

    /// <summary>Creates a person with one device and returns who they are and their token.</summary>
    public async Task<(Principal Principal, string Token)> AddPersonAsync(
        string name, PersonRole role = PersonRole.Member, DeviceKind kind = DeviceKind.App)
    {
        var person = await People.CreatePersonAsync(Admin, name, role);
        var issued = await People.IssueDeviceAsync(person.Id, name + "'s phone", "android", kind);
        return ((await People.AuthenticateAsync(issued.Token))!, issued.Token);
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null, string? token = null) =>
        Http.SendAsync(Request(method, path, body, token));

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<string> Code(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("code").GetString()!;

    public static string Body(object value) => JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        AdminHttp.Dispose();
        await Host.DisposeAsync();
        if (Extensions is not null) await Extensions.StopAllAsync();
        if (Root is not null)
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
        Questions.Dispose();
        People.Dispose();
        Database.Dispose();
    }
}

/// <summary>Keeps every log line the host writes, at every level.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return [.. _lines]; }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(CapturingLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines) owner._lines.Add(formatter(state, exception) + " " + exception);
        }
    }
}
