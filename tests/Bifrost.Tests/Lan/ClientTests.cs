using System.Net;
using System.Net.Sockets;
using Bifrost.Client;
using Bifrost.Core.Api;
using Bifrost.Server.Hosting;
using Bifrost.Server.Security;
using Bifrost.Tests.Server;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bifrost.Tests.Lan;

public class ClientTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static HomeKey NewKey() => HomeKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "bifrost-cl-" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void A_link_is_parsed_and_a_bad_one_is_refused()
    {
        var link = PairingLink.Parse($"bifrost://pair?h=192.168.1.5%3A7781&fp={new string('a', 64)}&t=sec_ret&n=Home");
        Assert.Equal(("192.168.1.5:7781", "sec_ret", "Home"), (link.Address, link.Secret, link.HomeName));
        Assert.Throws<FormatException>(() => PairingLink.Parse("https://example.com"));
        Assert.Throws<FormatException>(() => PairingLink.Parse("bifrost://pair?h=x&fp=short&t=y"));
    }

    [Fact]
    public async Task Pairing_with_a_link_pins_the_key_and_a_wrong_key_gets_no_secret()
    {
        using var key = NewKey();
        var port = FreePort();
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, port));
        home.Http.DefaultRequestHeaders.Authorization = new("Bearer", home.Token);
        var invitation = await (await home.Http.PostAsJsonAsync("/api/v1/invitations", new { personId = home.Admin.PersonId })).Content.ReadFromJsonAsync<JsonElement>();
        var link = PairingLink.Parse(invitation.GetProperty("link").GetString()!.Replace("localhost:" + port, "127.0.0.1:" + port));

        await Assert.ThrowsAsync<BifrostPairing.PairingRefusedException>(() =>
            BifrostPairing.PairWithLinkAsync(link with { Fingerprint = new string('0', 64) }, "x", "y")); // wrong key: the secret is never sent

        var paired = await BifrostPairing.PairWithLinkAsync(link, "Phone", "android");
        Assert.Equal(key.Fingerprint, paired.Fingerprint);
        using var pinned = PinnedHttp.Create(paired.Fingerprint, paired.Address);
        pinned.DefaultRequestHeaders.Authorization = new("Bearer", paired.Token);
        Assert.Equal(HttpStatusCode.OK, (await pinned.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Joining_by_code_shows_the_code_the_admin_sees_and_gets_the_token_once_approved()
    {
        using var key = NewKey();
        var port = FreePort();
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, port));
        home.Http.DefaultRequestHeaders.Authorization = new("Bearer", home.Token);
        string? shown = null;

        var joining = BifrostPairing.JoinAsync($"127.0.0.1:{port}", "TV", "tizen", code => shown = code, TimeSpan.FromMilliseconds(100));
        JsonElement pending = default;
        for (var i = 0; i < 100 && pending.ValueKind != JsonValueKind.Object; i++)
        {
            var list = await home.Http.GetFromJsonAsync<JsonElement>("/api/v1/join-requests");
            if (list.GetArrayLength() > 0) pending = list[0];
            else await Task.Delay(50);
        }
        Assert.Equal(shown, pending.GetProperty("code").GetString()); // the person compares the two
        await home.Http.PostAsJsonAsync($"/api/v1/join-requests/{pending.GetProperty("id").GetGuid()}/approve", new { personId = home.Admin.PersonId });

        var paired = await joining.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(key.Fingerprint, paired.Fingerprint);
    }
}
