using System.Net;
using System.Text;
using System.Text.Json;
using Bifrost.Server.Api;
using Bifrost.Server.Extensions;
using Bifrost.Server.Persistence;
using Bifrost.Tests.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Tests.Questions;

/// <summary>An extension asks on its socket; admins see and answer on the loopback API and the admin socket.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync")]
public sealed class QuestionRouteTests : IAsyncLifetime
{
    private static readonly string[] Names = ["sleipnir", "saga"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bifrost-q-" + Guid.NewGuid().ToString("N")[..8]);
    private HostFixture _home = null!;
    private ExtensionApiHost _api = null!;
    private HttpClient _sleipnir = null!;
    private HttpClient _saga = null!;

    public async Task InitializeAsync()
    {
        _home = await HostFixture.StartAsync();
        var extensions = Names.Select(n => new ExtensionSupervisor(n, "/bin/false", Path.Combine(_root, "run"),
            Path.Combine(_root, "state"), TimeProvider.System, NullLogger.Instance)).ToList();
        _api = new ExtensionApiHost(extensions, _home.People, routes => QuestionEndpoints.MapForExtensions(routes, _home.Questions));
        await _api.StartAsync();
        _sleipnir = UnixHttp.Client(extensions[0].ApiSocketPath);
        _saga = UnixHttp.Client(extensions[1].ApiSocketPath);
    }

    public async Task DisposeAsync()
    {
        _sleipnir.Dispose();
        _saga.Dispose();
        await _api.DisposeAsync();
        await _home.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private object Question(int expiresInSeconds = 3600) => new
    {
        title = "Download for Kid?",
        text = "example.com, film.mkv",
        choices = new[] { new { id = "approve", label = "Approve" }, new { id = "reject", label = "Reject" } },
        expiresAt = _home.Time.GetUtcNow().UtcDateTime.AddSeconds(expiresInSeconds),
    };

    private async Task<JsonElement> AskAsync(HttpClient extension)
    {
        var response = await extension.PostAsync("/v1/questions", Json(Question()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await HostFixture.Json(response);
    }

    [Fact]
    public async Task An_extension_asks_the_admin_answers_and_the_long_poll_returns_the_choice()
    {
        var asked = await AskAsync(_sleipnir);
        var id = asked.GetProperty("id").GetGuid();
        Assert.Equal("sleipnir", asked.GetProperty("asker").GetString());
        Assert.Equal("open", asked.GetProperty("state").GetString());
        var waiting = _sleipnir.GetAsync($"/v1/questions/{id}?wait=30");

        var list = await HostFixture.Json(await _home.SendAsync(HttpMethod.Get, "/api/v1/questions"));
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.False(waiting.IsCompleted);
        var answer = await _home.SendAsync(HttpMethod.Post, $"/api/v1/questions/{id}/answer", HostFixture.Body(new { choice = "approve" }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var seen = await HostFixture.Json(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("answered", seen.GetProperty("state").GetString());
        Assert.Equal("approve", seen.GetProperty("choice").GetString());
        Assert.Equal(_home.Admin.PersonId, seen.GetProperty("answeredBy").GetGuid());
        await using var db = await _home.Database.CreateDbContextAsync();
        Assert.Equal("sleipnir:approve", (await db.Audit.SingleAsync(a => a.Kind == "question.answered")).Detail);
    }

    [Fact]
    public async Task The_desk_sees_a_question_appear_and_answers_on_the_admin_socket()
    {
        var empty = await HostFixture.Json(await _home.AdminHttp.GetAsync("/api/v1/questions"));
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
        var seq = empty.GetProperty("seq").GetInt64();
        var polling = _home.AdminHttp.GetAsync($"/api/v1/questions?after={seq}&wait=30");
        await Task.Delay(100);
        Assert.False(polling.IsCompleted);

        var asked = await AskAsync(_sleipnir);

        var list = await HostFixture.Json(await polling.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(list.GetProperty("seq").GetInt64() > seq);
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(asked.GetProperty("id").GetGuid(), id);

        var answer = await _home.AdminHttp.PostAsync($"/api/v1/questions/{id}/answer", Json(new { choice = "reject" }));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("reject", (await HostFixture.Json(await _sleipnir.GetAsync($"/v1/questions/{id}"))).GetProperty("choice").GetString());
    }

    [Fact]
    public async Task A_second_answer_gets_409_with_how_it_ended()
    {
        var id = (await AskAsync(_sleipnir)).GetProperty("id").GetGuid();
        await _home.AdminHttp.PostAsync($"/api/v1/questions/{id}/answer", Json(new { choice = "approve" }));

        var late = await _home.SendAsync(HttpMethod.Post, $"/api/v1/questions/{id}/answer", HostFixture.Body(new { choice = "reject" }));

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var body = await HostFixture.Json(late);
        Assert.Equal("already_closed", body.GetProperty("code").GetString());
        Assert.Equal("approve", body.GetProperty("question").GetProperty("choice").GetString());
    }

    [Fact]
    public async Task A_member_gets_403()
    {
        var id = (await AskAsync(_sleipnir)).GetProperty("id").GetGuid();
        var (_, member) = await _home.AddPersonAsync("Kid");

        var list = await _home.SendAsync(HttpMethod.Get, "/api/v1/questions", token: member);
        var answer = await _home.SendAsync(HttpMethod.Post, $"/api/v1/questions/{id}/answer", HostFixture.Body(new { choice = "approve" }), member);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("open", (await HostFixture.Json(await _sleipnir.GetAsync($"/v1/questions/{id}"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task One_extension_cannot_read_or_withdraw_anothers_question()
    {
        var id = (await AskAsync(_sleipnir)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await _saga.GetAsync($"/v1/questions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _saga.DeleteAsync($"/v1/questions/{id}")).StatusCode);
        Assert.Equal("open", (await HostFixture.Json(await _sleipnir.GetAsync($"/v1/questions/{id}"))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Withdrawing_removes_it_from_the_admins_list_and_a_late_answer_is_409()
    {
        var id = (await AskAsync(_sleipnir)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await _sleipnir.DeleteAsync($"/v1/questions/{id}")).StatusCode);

        var list = await HostFixture.Json(await _home.AdminHttp.GetAsync("/api/v1/questions"));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        var late = await _home.AdminHttp.PostAsync($"/api/v1/questions/{id}/answer", Json(new { choice = "approve" }));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("withdrawn", (await HostFixture.Json(late)).GetProperty("question").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Bad_questions_and_bad_answers_are_400()
    {
        var bad = await _sleipnir.PostAsync("/v1/questions", Json(new { title = "No choices", expiresAt = _home.Time.GetUtcNow().UtcDateTime.AddHours(1) }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_question", await HostFixture.Code(bad));

        var id = (await AskAsync(_sleipnir)).GetProperty("id").GetGuid();
        var wrong = await _home.AdminHttp.PostAsync($"/api/v1/questions/{id}/answer", Json(new { choice = "maybe" }));
        Assert.Equal("invalid_choice", await HostFixture.Code(wrong));
        var missing = await _home.AdminHttp.PostAsync($"/api/v1/questions/{Guid.NewGuid()}/answer", Json(new { choice = "approve" }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_question_routes_need_a_token_on_the_loopback_port()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _home.Http.GetAsync("/api/v1/questions")).StatusCode);
    }
}
