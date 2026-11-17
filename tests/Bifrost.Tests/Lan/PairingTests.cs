using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bifrost.Server.Hosting;
using Bifrost.Server.Security;
using Bifrost.Tests.Server;

namespace Bifrost.Tests.Lan;

public class PairingTests
{
    [Fact]
    public async Task An_invitation_pairs_one_device_once_and_the_token_works()
    {
        using var key = HomeKey.LoadOrCreate(Path.Combine(Path.GetTempPath(), "bifrost-pair-" + Guid.NewGuid().ToString("N")[..8]));
        await using var home = await HostFixture.StartAsync(lan: new LanOptions(key.Certificate, 0));
        home.Http.DefaultRequestHeaders.Authorization = new("Bearer", home.Token);

        using var invited = await home.Http.PostAsJsonAsync("/api/v1/invitations", new { personId = home.Admin.PersonId });
        var link = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!;
        Assert.Contains($"fp={key.Fingerprint}", link);
        var secret = link[(link.IndexOf("&t=", StringComparison.Ordinal) + 3)..link.IndexOf("&n=", StringComparison.Ordinal)];

        using var anonymous = new HttpClient { BaseAddress = home.Http.BaseAddress };
        using var paired = await anonymous.PostAsJsonAsync("/api/v1/pair", new { secret, name = "Kitchen tablet", platform = "android" });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var token = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var again = await anonymous.PostAsJsonAsync("/api/v1/pair", new { secret, name = "Other", platform = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode); // single use

        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/me")).StatusCode);
    }
}
