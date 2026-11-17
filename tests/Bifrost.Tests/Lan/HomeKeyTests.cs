using System.Net;
using System.Net.Sockets;
using Bifrost.Server.Hosting;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Bifrost.Server.Security;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Lan;

public class HomeKeyTests
{
    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "bifrost-key-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void The_key_is_made_once_kept_owner_only_and_rotation_changes_it()
    {
        var folder = TempFolder();
        using var first = HomeKey.LoadOrCreate(folder);
        using var again = HomeKey.LoadOrCreate(folder);
        Assert.Equal(first.Fingerprint, again.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, HomeKey.FileName)));

        using var rotated = HomeKey.Rotate(folder);
        Assert.NotEqual(first.Fingerprint, rotated.Fingerprint);
    }

    [Fact]
    public async Task The_home_network_listener_serves_https_with_the_pinned_key_and_refuses_foreign_hosts()
    {
        using var key = HomeKey.LoadOrCreate(TempFolder());
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var database = Database.OpenMemory();
        await Database.MigrateAsync(database);
        using var people = new PeopleService(database, new FakeTimeProvider());
        await using var host = BifrostHost.Create(new BifrostHostOptions(0, Lan: new LanOptions(key.Certificate, port)), people, new FakeTimeProvider());
        await host.StartAsync();

        using var client = new HttpClient(new HttpClientHandler
        {
            // The client pins the key's fingerprint, not a certificate chain (design §6.2).
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && HomeKey.FingerprintOf(cert) == key.Fingerprint,
        });
        using var ok = await client.GetAsync($"https://127.0.0.1:{port}/api/v1/server");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/api/v1/server") { Headers = { Host = "evil.example" } };
        using var refused = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
