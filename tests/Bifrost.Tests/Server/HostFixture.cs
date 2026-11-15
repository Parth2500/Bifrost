using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bifrost.Core.Models;
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
    public int Port => Host.Port;

    public static async Task<HostFixture> StartAsync(int port = 0)
    {
        var database = Bifrost.Server.Persistence.Database.OpenMemory();
        await Bifrost.Server.Persistence.Database.MigrateAsync(database);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var people = new PeopleService(database, time);
        var token = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..43];
        var admin = await people.EnsureLocalAdminAsync("admin", token);
        var logs = new CapturingLogs();

        var host = BifrostHost.Create(new BifrostHostOptions(port), people, time, logs);
        await host.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        return new HostFixture
        {
            Host = host, Http = http, Token = token, People = people, Admin = admin, Database = database, Time = time, Logs = logs,
        };
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
        await Host.DisposeAsync();
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
