using Bifrost.Core.Api;
using Bifrost.Core.Models;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Bifrost.Server.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Questions;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in IAsyncLifetime.DisposeAsync")]
public sealed class QuestionServiceTests : IAsyncLifetime
{
    private static readonly Choice[] YesNo = [new("approve", "Approve"), new("reject", "Reject")];
    private readonly BifrostDbFactory _db = Database.OpenMemory();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private PeopleService _people = null!;
    private QuestionService _questions = null!;
    private Principal _admin = null!;

    public async Task InitializeAsync()
    {
        await Database.MigrateAsync(_db);
        _people = new PeopleService(_db, _time);
        _admin = await _people.EnsureLocalAdminAsync("alice", "local-token");
        _questions = new QuestionService(_people, _time);
    }

    public Task DisposeAsync()
    {
        _questions.Dispose();
        _people.Dispose();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private DateTime In(TimeSpan span) => _time.GetUtcNow().UtcDateTime + span;

    private QuestionDto Ask(string asker = "sleipnir", TimeSpan? life = null) =>
        _questions.Ask(asker, "Download for Kid?", "example.com, film.mkv", YesNo, In(life ?? TimeSpan.FromHours(24)), online: false);

    private async Task<Principal> MemberAsync()
    {
        var person = await _people.CreatePersonAsync(_admin, "Kid", PersonRole.Member);
        var issued = await _people.IssueDeviceAsync(person.Id, "phone", "android", DeviceKind.App);
        return (await _people.AuthenticateAsync(issued.Token))!;
    }

    private async Task<List<AuditEntity>> AnswersAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.Audit.Where(a => a.Kind == "question.answered").OrderBy(a => a.Id).ToListAsync();
    }

    private static async Task<QuestionException> Refused(Func<Task> action) => await Assert.ThrowsAsync<QuestionException>(action);

    [Fact]
    public void A_valid_question_is_open_and_listed()
    {
        var question = Ask();

        Assert.Equal(QuestionState.Open, question.State);
        Assert.Equal("sleipnir", question.Asker);
        Assert.Equal(question, Assert.Single(_questions.ListOpen()));
        Assert.Equal(question, _questions.Get(question.Id));
        Assert.Equal(question.Seq, _questions.Seq);
    }

    public static TheoryData<string, string, string[], int> BadQuestions() => new()
    {
        { "", "text", ["a", "b"], 3600 },                              // no title
        { new string('t', 81), "text", ["a", "b"], 3600 },             // title too long
        { "title", new string('x', 501), ["a", "b"], 3600 },           // text too long
        { "title", "text", ["a"], 3600 },                              // one choice
        { "title", "text", ["a", "b", "c", "d", "e"], 3600 },          // five choices
        { "title", "text", ["a", "a"], 3600 },                         // the same choice twice
        { "title", "text", ["Approve", "b"], 3600 },                   // upper case in an id
        { "title", "text", ["a b", "b"], 3600 },                       // a space in an id
        { "title", "text", [new string('a', 33), "b"], 3600 },         // id too long
        { "title", "text", ["a", "b"], 59 },                           // expires too soon
        { "title", "text", ["a", "b"], 7 * 24 * 3600 + 1 },            // expires too late
        { "title", "text", ["a", "b"], -5 },                           // already past
        { "ti\u0007tle", "text", ["a", "b"], 3600 },                   // a control character
    };

    [Theory]
    [MemberData(nameof(BadQuestions))]
    public void Ask_validates_every_limit(string title, string text, string[] choiceIds, int expiresInSeconds)
    {
        var choices = choiceIds.Select(id => new Choice(id, "Label")).ToList();

        var e = Assert.Throws<QuestionException>(() =>
            _questions.Ask("sleipnir", title, text, choices, In(TimeSpan.FromSeconds(expiresInSeconds)), false));

        Assert.Equal("invalid_question", e.Code);
        Assert.Empty(_questions.ListOpen());
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void A_choice_label_must_be_1_to_40_characters(string label)
    {
        var e = Assert.Throws<QuestionException>(() =>
            _questions.Ask("sleipnir", "title", "text", [new Choice("a", label), new Choice("b", "B")], In(TimeSpan.FromHours(1)), false));

        Assert.Equal("invalid_question", e.Code);
    }

    [Fact]
    public void The_limits_themselves_are_allowed()
    {
        var question = _questions.Ask("sleipnir", new string('t', 80), new string('x', 500),
            [new("a", new string('l', 40)), new(new string('b', 32), "B"), new("c_-9", "C"), new("d", "D")], In(TimeSpan.FromDays(7)), true);

        Assert.Equal(4, question.Choices.Count);
        Assert.True(question.Online);
        Assert.NotNull(_questions.Ask("sleipnir", "t", "", YesNo, In(TimeSpan.FromMinutes(1)), false));
    }

    [Fact]
    public async Task The_first_answer_wins_and_is_audited()
    {
        var question = Ask();

        var answered = await _questions.AnswerAsync(question.Id, "approve", _admin);

        Assert.Equal(QuestionState.Answered, answered.State);
        Assert.Equal("approve", answered.Choice);
        Assert.Equal(_admin.PersonId, answered.AnsweredBy);
        Assert.Empty(_questions.ListOpen());
        var row = Assert.Single(await AnswersAsync());
        Assert.Equal(question.Id, row.SubjectId);
        Assert.Equal(_admin.PersonId, row.ActorPersonId);
        Assert.Equal(_admin.DeviceId, row.ActorDeviceId);
        Assert.Equal("sleipnir:approve", row.Detail);

        var late = await Refused(() => _questions.AnswerAsync(question.Id, "reject", _admin));
        Assert.Equal("already_closed", late.Code);
        Assert.Equal("approve", late.Question!.Choice);
        Assert.Single(await AnswersAsync());
    }

    [Fact]
    public async Task Two_answers_at_once_record_one()
    {
        for (var round = 0; round < 50; round++)
        {
            var question = Ask();
            using var barrier = new Barrier(2);
            async Task<string> Try(string choice)
            {
                barrier.SignalAndWait();
                try { return (await _questions.AnswerAsync(question.Id, choice, _admin)).Choice!; }
                catch (QuestionException e) { return e.Code; }
            }

            var results = await Task.WhenAll(Task.Run(() => Try("approve")), Task.Run(() => Try("reject")));

            Assert.Single(results, r => r == "already_closed");
            var winner = Assert.Single(results, r => r != "already_closed");
            Assert.Equal(winner, _questions.Get(question.Id)!.Choice);
            var rows = (await AnswersAsync()).Where(a => a.SubjectId == question.Id).ToList();
            Assert.Equal($"sleipnir:{winner}", Assert.Single(rows).Detail);
        }
    }

    [Fact]
    public async Task A_member_cannot_answer()
    {
        var question = Ask();
        var member = await MemberAsync();

        var e = await Refused(() => _questions.AnswerAsync(question.Id, "approve", member));

        Assert.Equal("forbidden", e.Code);
        Assert.Equal(QuestionState.Open, _questions.Get(question.Id)!.State);
        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task An_unknown_choice_or_question_is_refused()
    {
        var question = Ask();

        Assert.Equal("invalid_choice", (await Refused(() => _questions.AnswerAsync(question.Id, "maybe", _admin))).Code);
        Assert.Equal("question_not_found", (await Refused(() => _questions.AnswerAsync(Guid.NewGuid(), "approve", _admin))).Code);
        Assert.Equal(QuestionState.Open, _questions.Get(question.Id)!.State);
        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task Expiry_closes_the_question()
    {
        var question = Ask(life: TimeSpan.FromMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(QuestionState.Open, _questions.Get(question.Id)!.State);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(QuestionState.Expired, _questions.Get(question.Id)!.State);
        Assert.Empty(_questions.ListOpen());
        Assert.Equal("already_closed", (await Refused(() => _questions.AnswerAsync(question.Id, "approve", _admin))).Code);
    }

    [Fact]
    public async Task Withdraw_closes_it_and_only_for_its_asker()
    {
        var question = Ask(asker: "sleipnir");

        Assert.False(_questions.Withdraw(question.Id, "saga"));
        Assert.Equal(QuestionState.Open, _questions.Get(question.Id)!.State);

        Assert.True(_questions.Withdraw(question.Id, "sleipnir"));
        Assert.Equal(QuestionState.Withdrawn, _questions.Get(question.Id)!.State);
        Assert.False(_questions.Withdraw(question.Id, "sleipnir"));

        // A late click does nothing harmful.
        var late = await Refused(() => _questions.AnswerAsync(question.Id, "approve", _admin));
        Assert.Equal("already_closed", late.Code);
        Assert.Equal(QuestionState.Withdrawn, late.Question!.State);
        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task Waiters_wake_on_any_change_and_time_out_after_the_limit()
    {
        var start = _questions.Seq;
        Assert.Equal(start, await _questions.WaitForChangeAsync(start, TimeSpan.Zero, CancellationToken.None));

        var asked = _questions.WaitForChangeAsync(start, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(asked.IsCompleted);
        var question = Ask();
        Assert.Equal(question.Seq, await asked.WaitAsync(TimeSpan.FromSeconds(5)));

        // Already behind: returns at once.
        Assert.Equal(question.Seq, await _questions.WaitForChangeAsync(start, TimeSpan.FromSeconds(30), CancellationToken.None));

        var answered = _questions.WaitForChangeAsync(question.Seq, TimeSpan.FromSeconds(30), CancellationToken.None);
        await _questions.AnswerAsync(question.Id, "approve", _admin);
        Assert.True(await answered.WaitAsync(TimeSpan.FromSeconds(5)) > question.Seq);

        var quiet = _questions.WaitForChangeAsync(_questions.Seq, TimeSpan.FromMinutes(10), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(quiet.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1)); // 30 s is the most a wait lasts, whatever was asked
        Assert.Equal(_questions.Seq, await quiet.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Waiting_for_a_question_returns_when_it_closes()
    {
        var question = Ask();
        var waiting = _questions.WaitUntilClosedAsync(question.Id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Ask(asker: "saga"); // another question changing must not end the wait
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        _questions.Withdraw(question.Id, "sleipnir");

        Assert.Equal(QuestionState.Withdrawn, (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))!.State);
        Assert.Null(await _questions.WaitUntilClosedAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    [Fact]
    public void Asking_and_withdrawing_in_a_loop_does_not_grow_without_bound()
    {
        var other = Ask(asker: "saga");
        _questions.Withdraw(other.Id, "saga");

        for (var i = 0; i < 1000; i++) _questions.Withdraw(Ask().Id, "sleipnir");

        Assert.InRange(_questions.Count, 1, 201); // sleipnir's closed ones are capped at 200, plus saga's one
        Assert.NotNull(_questions.Get(other.Id)); // one asker's flood does not push out another's
    }

    [Fact]
    public void An_asker_cannot_hold_more_than_200_open_questions()
    {
        for (var i = 0; i < 200; i++) Ask();

        Assert.Equal("too_many_questions", Assert.Throws<QuestionException>(() => Ask()).Code);
        Assert.NotNull(Ask(asker: "saga")); // another asker is not affected
    }
}
