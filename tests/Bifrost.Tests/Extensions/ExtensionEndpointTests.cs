using System.Net;
using Bifrost.Core.Models;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Extensions;

public class ExtensionEndpointTests
{
    [Fact]
    public async Task The_admin_sees_each_extension_and_its_state()
    {
        await using var home = await StartAsync(extensions: new Dictionary<string, string> { ["sleipnir"] = "echo", ["saga"] = "hang" });
        await Until(home, "sleipnir", "running");

        var list = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/extensions"));

        var byName = list.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.Equal("running", byName["sleipnir"].GetProperty("state").GetString());
        Assert.Equal("starting", byName["saga"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKindNumber, byName["sleipnir"].GetProperty("pid").ValueKind);
        Assert.Equal(0, byName["sleipnir"].GetProperty("restarts").GetInt32());
    }

    [Fact]
    public async Task A_member_may_not_see_them()
    {
        await using var home = await StartAsync(extensions: new Dictionary<string, string> { ["sleipnir"] = "echo" });
        var (_, token) = await home.AddPersonAsync("bob", PersonRole.Member);

        var response = await home.SendAsync(HttpMethod.Get, "/api/v1/extensions", token: token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Without_extensions_the_list_is_empty()
    {
        await using var home = await StartAsync();

        Assert.Equal(0, (await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/extensions"))).GetArrayLength());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNumber = System.Text.Json.JsonValueKind.Number;

    private static async Task Until(Bifrost.Tests.Server.HostFixture home, string name, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var list = await Json(await home.SendAsync(HttpMethod.Get, "/api/v1/extensions"));
            if (list.EnumerateArray().Any(e => e.GetProperty("name").GetString() == name && e.GetProperty("state").GetString() == state)) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{name} never became {state}");
            await Task.Delay(50);
        }
    }
}
