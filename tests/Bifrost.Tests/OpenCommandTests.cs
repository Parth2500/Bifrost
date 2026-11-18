using Bifrost.App;
using Bifrost.App.Service;

namespace Bifrost.Tests;

public sealed class OpenCommandTests
{
    private sealed class Recording : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public string? FileSeen { get; private set; }
        public UnixFileMode? ModeSeen { get; private set; }
        public int Exit { get; init; }

        public Task<(int Exit, string Output)> RunAsync(string file, params string[] args)
        {
            Calls.Add([file, .. args]);
            if (args.Length == 1 && File.Exists(args[0]))
            {
                FileSeen = File.ReadAllText(args[0]);
                ModeSeen = File.GetUnixFileMode(args[0]);
            }
            return Task.FromResult((Exit, ""));
        }
    }

    [Fact]
    public async Task The_secret_never_goes_in_a_process_argument_and_the_file_is_private_and_removed()
    {
        var dir = Directory.CreateTempSubdirectory("bifrost-open-").FullName;
        var runner = new Recording();
        const string url = "http://127.0.0.1:7780/host/login?t=SECRETSECRETSECRET";

        var opened = await OpenCommand.OpenInBrowserAsync(url, runner, dir);

        Assert.True(opened);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("xdg-open", call[0]);
        Assert.DoesNotContain(call, a => a.Contains("SECRET", StringComparison.Ordinal)); // the arguments carry no secret
        Assert.Contains("SECRETSECRETSECRET", runner.FileSeen); // the private file does
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, runner.ModeSeen);
        Assert.Empty(Directory.GetFiles(dir)); // and it is gone afterwards
        Directory.Delete(dir);
    }

    [Fact]
    public async Task Without_a_private_folder_nothing_is_opened_and_no_secret_is_handed_over()
    {
        var runner = new Recording();

        Assert.False(await OpenCommand.OpenInBrowserAsync("http://127.0.0.1:1/host/login?t=x", runner, null));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_browser_that_will_not_start_is_reported_and_the_file_is_still_removed()
    {
        var dir = Directory.CreateTempSubdirectory("bifrost-open-").FullName;

        Assert.False(await OpenCommand.OpenInBrowserAsync("http://127.0.0.1:1/host/login?t=x", new Recording { Exit = 127 }, dir));
        Assert.Empty(Directory.GetFiles(dir));
        Directory.Delete(dir);
    }

    [Fact]
    public void The_launcher_sends_the_browser_on_and_escapes_the_link()
    {
        var html = OpenCommand.Launcher("http://127.0.0.1:1/host/login?t=a&b=\"x");

        Assert.Contains("http-equiv=\"refresh\"", html);
        Assert.Contains("t=a&amp;b=&quot;x", html);
    }
}
