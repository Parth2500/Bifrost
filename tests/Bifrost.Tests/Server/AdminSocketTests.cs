using System.Net;
using System.Net.Http.Headers;
using Bifrost.Server.Security;

namespace Bifrost.Tests.Server;

/// <summary>The admin socket (design §5): the operating system says who is calling, and only the admin account gets in.</summary>
public class AdminSocketTests
{
    [Fact]
    public async Task The_admin_account_on_the_admin_socket_is_the_host_admin()
    {
        await using var home = await HostFixture.StartAsync();

        var me = await HostFixture.Json(await home.AdminHttp.GetAsync("/api/v1/me"));

        Assert.Equal("This computer", me.GetProperty("deviceName").GetString());
        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.Equal(home.Admin.PersonId, me.GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task The_host_admin_can_manage_people_without_a_token()
    {
        await using var home = await HostFixture.StartAsync();

        using var body = new StringContent(HostFixture.Body(new { displayName = "Kid" }), System.Text.Encoding.UTF8, "application/json");
        var created = await home.AdminHttp.PostAsync("/api/v1/people", body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Another_account_is_refused()
    {
        var mine = PeerCredentials.CurrentUid();
        await using var home = await HostFixture.StartAsync(adminUid: mine + 1);

        var response = await home.AdminHttp.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_the_admin", await HostFixture.Code(response));
        Assert.Contains(home.Logs.Lines, line =>
            line.Contains("admin socket", StringComparison.Ordinal)
            && line.Contains(mine.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_account_gets_nothing_at_all_not_even_the_public_routes()
    {
        await using var home = await HostFixture.StartAsync(adminUid: PeerCredentials.CurrentUid() + 1);

        foreach (var path in new[] { "/api/v1/server", "/openapi/v1.json", "/scalar", "/x/sleipnir/hello" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await home.AdminHttp.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Another_account_is_refused_even_with_the_admins_token()
    {
        await using var home = await HostFixture.StartAsync(adminUid: PeerCredentials.CurrentUid() + 1);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);

        var response = await home.AdminHttp.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_on_the_admin_socket_changes_nothing()
    {
        await using var home = await HostFixture.StartAsync();
        var (_, memberToken) = await home.AddPersonAsync("Kid");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);

        var me = await HostFixture.Json(await home.AdminHttp.SendAsync(request));

        Assert.Equal("This computer", me.GetProperty("deviceName").GetString());
        Assert.Equal("admin", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_loopback_port_still_needs_a_token()
    {
        await using var home = await HostFixture.StartAsync();

        var response = await home.Http.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_socket_is_0666_in_a_0755_folder()
    {
        await using var home = await HostFixture.StartAsync();

        Assert.Equal((UnixFileMode)0b110_110_110, File.GetUnixFileMode(home.AdminSocket));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Path.GetDirectoryName(home.AdminSocket)!));
    }

    [Fact]
    public async Task The_host_check_holds_on_the_admin_socket_too()
    {
        await using var home = await HostFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Host = "evil.example";

        var response = await home.AdminHttp.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Without_a_socket_there_are_no_credentials()
    {
        Assert.Null(PeerCredentials.Of(null));
    }

    [Fact]
    public void A_connected_unix_socket_names_this_process_and_account()
    {
        var path = Path.Combine(Path.GetTempPath(), "bifrost-peer-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        try
        {
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            using var accepted = listener.Accept();

            var peer = PeerCredentials.Of(accepted);

            Assert.NotNull(peer);
            Assert.Equal(Environment.ProcessId, peer.Value.Pid);
            Assert.Equal(PeerCredentials.CurrentUid(), peer.Value.Uid);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
