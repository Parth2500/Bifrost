using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Bifrost.Core.Models;
using Bifrost.Server.Extensions;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Extensions;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync")]
public sealed class ExtensionApiTests : IAsyncLifetime
{
    private static readonly string[] Names = ["sleipnir", "saga"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bifrost-api-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly BifrostDbFactory _db = Database.OpenMemory();
    private PeopleService _people = null!;
    private Principal _admin = null!;
    private ExtensionApiHost _host = null!;
    private List<ExtensionSupervisor> _extensions = null!;

    public async Task InitializeAsync()
    {
        await Database.MigrateAsync(_db);
        _people = new PeopleService(_db, new FakeTimeProvider());
        _admin = await _people.EnsureLocalAdminAsync("alice", "t");
        _extensions = Names.Select(n => new ExtensionSupervisor(n, "/bin/false", Path.Combine(_root, "run"),
            Path.Combine(_root, "state"), TimeProvider.System, NullLogger.Instance)).ToList();
        _host = new ExtensionApiHost(_extensions, _people, routes => routes.MapGet("/test/who", (HttpContext c) => c.Extension()));
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _people.Dispose();
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static HttpClient ClientFor(string socket) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
            return new NetworkStream(s, ownsSocket: true);
        },
    }) { BaseAddress = new Uri("http://localhost") };

    [Fact]
    public async Task Looks_up_a_person_by_id()
    {
        var bob = await _people.CreatePersonAsync(_admin, "bob", PersonRole.Member);
        using var http = ClientFor(_extensions[0].ApiSocketPath);

        var json = JsonDocument.Parse(await http.GetStringAsync($"/v1/people/{bob.Id}")).RootElement;

        Assert.Equal(bob.Id, json.GetProperty("id").GetGuid());
        Assert.Equal("bob", json.GetProperty("displayName").GetString());
        Assert.Equal("member", json.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Looks_up_a_device_by_id()
    {
        var bob = await _people.CreatePersonAsync(_admin, "bob", PersonRole.Member);
        var tv = await _people.IssueDeviceAsync(bob.Id, "TV", "android-tv", DeviceKind.BrowserFallback);
        using var http = ClientFor(_extensions[0].ApiSocketPath);

        var json = JsonDocument.Parse(await http.GetStringAsync($"/v1/devices/{tv.Device.Id}")).RootElement;

        Assert.Equal(bob.Id, json.GetProperty("personId").GetGuid());
        Assert.Equal("TV", json.GetProperty("name").GetString());
        Assert.Equal("browser-fallback", json.GetProperty("kind").GetString());
        Assert.False(json.TryGetProperty("tokenHash", out _));
    }

    [Fact]
    public async Task Unknown_people_and_devices_are_404()
    {
        using var http = ClientFor(_extensions[0].ApiSocketPath);

        var person = await http.GetAsync($"/v1/people/{Guid.NewGuid()}");
        var device = await http.GetAsync($"/v1/devices/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, person.StatusCode);
        Assert.Contains("person_not_found", await person.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("device_not_found", await device.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sockets_are_owner_only()
    {
        foreach (var e in _extensions)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(e.ApiSocketPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(e.ApiSocketPath)!));
        }
    }

    [Fact]
    public async Task Each_request_knows_which_extension_asked()
    {
        using var sleipnir = ClientFor(_extensions[0].ApiSocketPath);
        using var saga = ClientFor(_extensions[1].ApiSocketPath);

        Assert.Equal("sleipnir", await sleipnir.GetStringAsync("/test/who"));
        Assert.Equal("saga", await saga.GetStringAsync("/test/who"));
    }
}
