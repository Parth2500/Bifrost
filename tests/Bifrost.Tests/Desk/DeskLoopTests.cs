using System.Collections.Concurrent;
using Bifrost.App.Desk;
using Bifrost.App.Service;
using Bifrost.Core.Api;
using Bifrost.Server.Questions;
using Bifrost.Tests.Server;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Desk;

public class DeskLoopTests
{
    private static readonly Choice[] YesNo = [new("approve", "Approve"), new("reject", "Reject")];

    /// <summary>Records what was shown; the test plays the admin.</summary>
    private sealed class FakeNotifier : INotifier
    {
        public ConcurrentQueue<Guid> Shown { get; } = new();
        public ConcurrentDictionary<Guid, TaskCompletionSource<string?>> Clicks { get; } = new();
        public ConcurrentDictionary<Guid, bool> Closed { get; } = new();

        /// <summary>When false, a notification stays "clickable" after it was closed, like a click that raced the close.</summary>
        public bool CloseEndsIt { get; set; } = true;

        public Task<string?> ShowAsync(Guid questionId, string title, string text, IReadOnlyList<Choice> choices, CancellationToken closeWhen)
        {
            Shown.Enqueue(questionId);
            var click = Clicks.GetOrAdd(questionId, _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
            closeWhen.Register(() =>
            {
                Closed[questionId] = true;
                if (CloseEndsIt) click.TrySetResult(null);
            });
            return click.Task;
        }

        public void Click(Guid id, string choice) => Clicks[id].TrySetResult(choice);
    }

    private static async Task Until(Func<bool> condition, string what, Action? eachTime = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never happened: " + what);
            eachTime?.Invoke();
            await Task.Delay(20);
        }
    }

    private static QuestionDto Ask(HostFixture home, string title = "Download for Kid?") =>
        home.Questions.Ask("sleipnir", title, "example.com, film.mkv", YesNo, home.Time.GetUtcNow().UtcDateTime.AddHours(1), false);

    [Fact]
    public async Task A_new_question_is_shown_once_and_the_click_answers_it()
    {
        await using var home = await HostFixture.StartAsync();
        var notifier = new FakeNotifier();
        using var stop = new CancellationTokenSource();
        var loop = new DeskLoop(home.AdminHttp, notifier, TimeProvider.System).RunAsync(stop.Token);

        var question = Ask(home);
        await Until(() => notifier.Shown.Contains(question.Id), "the question is shown");
        var second = Ask(home, "Another?"); // more changes go by while the first is on screen
        await Until(() => notifier.Shown.Contains(second.Id), "the second question is shown");
        notifier.Click(question.Id, "approve");

        await Until(() => home.Questions.Get(question.Id)!.State == QuestionState.Answered, "the click answers it");
        Assert.Equal("approve", home.Questions.Get(question.Id)!.Choice);
        Assert.Equal(home.Admin.PersonId, home.Questions.Get(question.Id)!.AnsweredBy);
        Assert.Single(notifier.Shown, id => id == question.Id);
        Assert.Equal(QuestionState.Open, home.Questions.Get(second.Id)!.State);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(notifier.Closed.ContainsKey(second.Id)); // stopping takes what is left off the screen
    }

    [Fact]
    public async Task A_question_answered_elsewhere_closes_its_notification()
    {
        await using var home = await HostFixture.StartAsync();
        var notifier = new FakeNotifier();
        using var stop = new CancellationTokenSource();
        var loop = new DeskLoop(home.AdminHttp, notifier, TimeProvider.System).RunAsync(stop.Token);
        var answeredOnAPhone = Ask(home);
        var withdrawn = Ask(home, "Second?");
        await Until(() => notifier.Shown.Count == 2, "both are shown");

        await home.Questions.AnswerAsync(answeredOnAPhone.Id, "reject", home.Admin);
        home.Questions.Withdraw(withdrawn.Id, "sleipnir");

        await Until(() => notifier.Closed.ContainsKey(answeredOnAPhone.Id) && notifier.Closed.ContainsKey(withdrawn.Id), "both notifications close");
        Assert.Equal("reject", home.Questions.Get(answeredOnAPhone.Id)!.Choice); // the desk did not answer over it
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_click_after_the_question_closed_does_no_harm()
    {
        await using var home = await HostFixture.StartAsync();
        var notifier = new FakeNotifier { CloseEndsIt = false };
        using var stop = new CancellationTokenSource();
        var loop = new DeskLoop(home.AdminHttp, notifier, TimeProvider.System).RunAsync(stop.Token);
        var question = Ask(home);
        await Until(() => notifier.Shown.Contains(question.Id), "the question is shown");
        home.Questions.Withdraw(question.Id, "sleipnir");
        await Until(() => notifier.Closed.ContainsKey(question.Id), "the desk learns it closed");

        notifier.Click(question.Id, "approve"); // the admin was a moment too late

        // The loop goes on: a later question is still shown and answered.
        var next = Ask(home, "Next?");
        await Until(() => notifier.Shown.Contains(next.Id), "the next question is shown");
        notifier.Click(next.Id, "approve");
        await Until(() => home.Questions.Get(next.Id)!.State == QuestionState.Answered, "the next click answers");
        Assert.Equal(QuestionState.Withdrawn, home.Questions.Get(question.Id)!.State);
        Assert.False(loop.IsCompleted);
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Without_Bifrost_the_loop_retries_and_then_shows_the_open_questions_once()
    {
        var socket = Path.Combine(Path.GetTempPath(), "bifrost-nobody-" + Guid.NewGuid().ToString("N")[..8], "admin.sock");
        var notifier = new FakeNotifier();
        var deskTime = new FakeTimeProvider();
        using var http = DeskCommand.Client(() => socket);
        using var stop = new CancellationTokenSource();
        var loop = new DeskLoop(http, notifier, deskTime).RunAsync(stop.Token);

        // Bifröst is not there: the desk waits, longer each time, and does not give up.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(30);
            deskTime.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.False(loop.IsCompleted);
        Assert.Empty(notifier.Shown);

        await using var home = await HostFixture.StartAsync();
        var first = Ask(home);
        var second = Ask(home, "Second?");
        socket = home.AdminSocket; // Bifröst is up now

        await Until(() => notifier.Shown.Count >= 2, "the open questions are shown", () => deskTime.Advance(TimeSpan.FromSeconds(30)));
        await Task.Delay(200);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), notifier.Shown.Order());
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_desk_unit_starts_with_the_graphical_session()
    {
        var unit = DeskUnit.Render("/opt/bifrost/bifrost");

        Assert.Contains("\nExecStart=/opt/bifrost/bifrost desk\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nRestart=always\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nWantedBy=graphical-session.target\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("User=", unit, StringComparison.Ordinal); // a user unit: it runs as whoever enabled it
    }

    [Fact]
    public void The_notification_escapes_markup_and_passes_choices_as_actions()
    {
        var arguments = NotifySendNotifier.Arguments("-Download for <Kid>?", "a.example & <b>film</b>", YesNo);

        Assert.Equal(
            ["--app-name=Bifröst", "--print-id", "--wait", "--action=approve=Approve", "--action=reject=Reject", "--",
                "-Download for <Kid>?", "a.example &amp; &lt;b&gt;film&lt;/b&gt;"],
            arguments);
    }

    [Fact]
    public void The_desk_looks_for_the_service_first_then_the_users_own_daemon()
    {
        var own = Path.Combine(Path.GetTempPath(), "bifrost-own-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        Assert.Equal("/custom.sock", DeskCommand.AdminSocket(name => name == "BIFROST_ADMIN_SOCKET" ? "/custom.sock" : null, own));
        if (File.Exists(DeskCommand.ServiceSocket)) return; // a machine with Bifröst installed always picks the service
        Assert.Equal(DeskCommand.ServiceSocket, DeskCommand.AdminSocket(_ => null, own)); // nothing yet: wait for the service
        File.WriteAllText(own, "");
        try
        {
            Assert.Equal(own, DeskCommand.AdminSocket(_ => null, own));
        }
        finally
        {
            File.Delete(own);
        }
    }
}

public sealed class DeskCommandTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly List<string> _calls = [];
    private readonly Files _files = new();

    public void Dispose() => _output.Dispose();

    private sealed class Runner(List<string> calls) : ICommandRunner
    {
        public Task<(int Exit, string Output)> RunAsync(string file, params string[] args)
        {
            calls.Add(file + " " + string.Join(' ', args));
            return Task.FromResult((0, ""));
        }
    }

    private sealed class Files : IFileSystem
    {
        public Dictionary<string, string> Written { get; } = [];
        public HashSet<string> Existing { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool FileExists(string path) => Existing.Contains(path) || Written.ContainsKey(path);
        public IReadOnlyList<string> FilesEndingWith(string folder, string suffix) => [];
        public void CreateDirectory(string path, UnixFileMode mode) { }
        public void WriteFile(string path, string content, UnixFileMode mode) => Written[path] = content;
        public void CopyFile(string source, string target, UnixFileMode mode) => Written[target] = "";
        public void Delete(string path) => Deleted.Add(path);
    }

    private DeskCommand Command(string processPath = "/home/parth/publish/bifrost", bool isRoot = false) =>
        new(new Runner(_calls), processPath, _ => null, "/home/parth", isRoot, _output, _files);

    [Fact]
    public async Task Install_writes_a_user_unit_and_starts_it()
    {
        var code = await Command().RunAsync(["install"]);

        Assert.Equal(0, code);
        Assert.Equal(DeskUnit.Render("/home/parth/publish/bifrost"), _files.Written["/home/parth/.config/systemd/user/bifrost-desk.service"]);
        Assert.Equal(["systemctl --user daemon-reload", "systemctl --user enable --now bifrost-desk.service"], _calls);
    }

    [Fact]
    public async Task Install_prefers_the_installed_program()
    {
        _files.Existing.Add("/opt/bifrost/bifrost");

        await Command(processPath: "/usr/lib64/dotnet/dotnet").RunAsync(["install"]);

        Assert.Contains("ExecStart=/opt/bifrost/bifrost desk", _files.Written["/home/parth/.config/systemd/user/bifrost-desk.service"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/lib64/dotnet/dotnet", false)]
    [InlineData("/home/parth/my apps/bifrost", false)]
    [InlineData("/home/parth/publish/bifrost", true)]
    public async Task Install_refuses_from_dotnet_from_an_unnameable_path_and_as_root(string processPath, bool isRoot)
    {
        var code = await Command(processPath, isRoot).RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Empty(_files.Written);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Uninstall_stops_it_and_removes_the_unit()
    {
        var code = await Command().RunAsync(["uninstall"]);

        Assert.Equal(0, code);
        Assert.Equal(["/home/parth/.config/systemd/user/bifrost-desk.service"], _files.Deleted);
        Assert.Equal(["systemctl --user disable --now bifrost-desk.service", "systemctl --user daemon-reload"], _calls);
    }

    [Fact]
    public async Task Anything_else_is_a_usage_error()
    {
        Assert.Equal(64, await Command().RunAsync(["frobnicate"]));
    }
}
