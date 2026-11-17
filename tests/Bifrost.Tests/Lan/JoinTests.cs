using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.Server.Hosting;
using Bifrost.Server.People;
using Bifrost.Server.Security;
using Bifrost.Tests.Server;

namespace Bifrost.Tests.Lan;

public class JoinTests
{
    private static string Commit(string nonce) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nonce)));

    [Fact]
    public async Task A_device_joins_when_the_admin_sees_the_same_code_and_a_refused_one_gets_nothing()
    {
        using var key = HomeKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "bifrost-join-" + Guid.NewGuid().ToString("N")[..8]));
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, 0));
        home.Http.DefaultRequestHeaders.Authorization = new("Bearer", home.Token);
        using var device = new HttpClient { BaseAddress = home.Http.BaseAddress };
        var nonce = Guid.NewGuid().ToString("N");

        var started = await (await device.PostAsJsonAsync("/api/v1/join", new { name = "TV", platform = "tizen", commit = Commit(nonce) })).Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("requestId").GetGuid();
        using var early = await home.Http.GetFromJsonAsync<JsonDocument>("/api/v1/join-requests");
        Assert.Equal(0, early!.RootElement.GetArrayLength()); // no code to compare before the nonce is revealed
        using var wrongReveal = await device.PostAsJsonAsync($"/api/v1/join/{id}/reveal", new { nonce = "not-the-committed-one" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongReveal.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"/api/v1/join/{id}/reveal", new { nonce })).StatusCode);
        var mine = JoinService.Code(System.Security.Cryptography.SHA256.HashData(key.Certificate.PublicKey.ExportSubjectPublicKeyInfo()), nonce, started.GetProperty("serverNonce").GetString()!);

        var listed = await home.Http.GetFromJsonAsync<JsonElement>("/api/v1/join-requests");
        Assert.Equal(mine, listed[0].GetProperty("code").GetString()); // the admin compares this with the TV's screen

        using var waiting = await device.PostAsJsonAsync($"/api/v1/join/{id}/collect", new { nonce });
        Assert.Equal(HttpStatusCode.Accepted, waiting.StatusCode);
        using var wrongNonce = await device.PostAsJsonAsync($"/api/v1/join/{id}/collect", new { nonce = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongNonce.StatusCode);

        using var approved = await home.Http.PostAsJsonAsync($"/api/v1/join-requests/{id}/approve", new { personId = home.Admin.PersonId });
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        using var done = await device.PostAsJsonAsync($"/api/v1/join/{id}/collect", new { nonce });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        using var twice = await device.PostAsJsonAsync($"/api/v1/join/{id}/collect", new { nonce });
        Assert.Equal(HttpStatusCode.Unauthorized, twice.StatusCode); // the token is handed over once

        var other = await (await device.PostAsJsonAsync("/api/v1/join", new { name = "Stranger", platform = "x", commit = Commit("another-nonce-of-length") })).Content.ReadFromJsonAsync<JsonElement>();
        var otherId = other.GetProperty("requestId").GetGuid();
        await home.Http.DeleteAsync($"/api/v1/join-requests/{otherId}");
        var refused = await device.PostAsJsonAsync($"/api/v1/join/{otherId}/collect", new { nonce = "unused" });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode); // wrong nonce looks like unknown, not like a refusal

        await using var db = await home.Database.CreateDbContextAsync();
        var kinds = db.Audit.Select(a => a.Kind).ToList();
        Assert.Contains("join.approved", kinds);
        Assert.Contains("join.rejected", kinds);
    }
}
