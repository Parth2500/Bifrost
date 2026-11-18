using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Core.Api;
using Bifrost.Server.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Extensions;

/// <summary>Releases from GitHub, with the network faked: what is asked for, what is trusted, what is offered (ADR 004).</summary>
public sealed class ExtensionUpdatesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bifrost-updates-").FullName;
    private readonly string _public;
    private readonly string _private;
    private readonly Dictionary<string, (string Version, byte[] Package, byte[] Signature)> _releases = [];
    private readonly List<string> _requests = [];
    private bool _offline;

    public ExtensionUpdatesTests()
    {
        _public = ReleaseBuilder.WriteKeyPair(_dir, "owner");
        _private = File.ReadAllText(Path.Combine(_dir, "owner.key"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Publish(string version, string? signedBy = null, bool withSignature = true, string name = "sleipnir")
    {
        var program = Path.Combine(_dir, "program");
        File.WriteAllText(program, "#!/bin/sh\n");
        var package = Path.Combine(_dir, $"{name}-{version}.bifrost");
        ReleaseBuilder.Pack(package, name, version, new Dictionary<string, string> { ["linux-x64"] = program, ["linux-arm64"] = program });
        var key = signedBy ?? _private;
        _releases[name] = (version, File.ReadAllBytes(package), withSignature ? ReleaseBuilder.Sign(key, package) : []);
    }

    private HttpClient Github() => new(new Fake(this));

    private sealed class Fake(ExtensionUpdatesTests t) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            t._requests.Add(request.RequestUri!.ToString());
            if (t._offline) throw new HttpRequestException("no network");
            var path = request.RequestUri!.AbsolutePath;
            foreach (var (name, release) in t._releases)
            {
                var repo = ExtensionUpdates.Repositories[name];
                if (path == $"/repos/{repo}/releases/latest")
                {
                    var assets = new List<string> { Asset($"{name}-{release.Version}.bifrost", $"https://downloads.example/{name}.bifrost") };
                    if (release.Signature.Length > 0) assets.Add(Asset($"{name}-{release.Version}.bifrost.sig", $"https://downloads.example/{name}.sig"));
                    return Task.FromResult(Json($$"""{"tag_name":"v{{release.Version}}","assets":[{{string.Join(",", assets)}}]}"""));
                }
                if (path == $"/{name}.bifrost") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Package) });
                if (path == $"/{name}.sig") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Signature) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string Asset(string name, string url) => $$"""{"name":"{{name}}","browser_download_url":"{{url}}"}""";

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private ExtensionInstaller Installer() => new(Path.Combine(_dir, "installed"), new ReleaseKeys([_public]));

    private ExtensionUpdates Updates(ExtensionInstaller installer, Bifrost.Server.Questions.QuestionService questions, TimeProvider time, Func<string, Task>? applied = null) =>
        new(installer, questions, Github(), time, NullLogger<ExtensionUpdates>.Instance, applied);

    [Fact]
    public async Task The_latest_release_needs_a_package_and_a_signature_and_only_the_owners_repositories_are_asked()
    {
        await using var home = await StartAsync();
        var updates = Updates(Installer(), home.Questions, home.Time);

        Assert.Null(await updates.LatestAsync("sleipnir", default)); // no release yet: GitHub says 404
        Publish("0.11.18", withSignature: false);
        Assert.Null(await updates.LatestAsync("sleipnir", default)); // a package nobody signed is not a release
        Publish("0.11.18");
        Assert.Equal("0.11.18", (await updates.LatestAsync("sleipnir", default))!.Version);

        Assert.Equal("no_source", (await Assert.ThrowsAsync<ExtensionUpdates.UpdateException>(() => updates.LatestAsync("files", default))).Code);
        Assert.All(_requests, r => Assert.StartsWith("https://api.github.com/repos/Parth2500/Sleipnir/", r));
    }

    [Fact]
    public async Task A_release_signed_by_the_owner_is_installed_and_one_signed_by_anyone_else_is_not()
    {
        await using var home = await StartAsync();
        var installer = Installer();
        var updates = Updates(installer, home.Questions, home.Time);

        Publish("0.11.18", signedBy: ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem());
        await Assert.ThrowsAsync<ExtensionInstaller.InstallRefusedException>(() => updates.InstallLatestAsync("sleipnir", default));
        Assert.Null(installer.CurrentVersion("sleipnir"));

        Publish("0.11.18");
        Assert.Equal("0.11.18", (await updates.InstallLatestAsync("sleipnir", default)).Version);
        Assert.Equal("0.11.18", installer.CurrentVersion("sleipnir"));
    }

    [Fact]
    public async Task Installing_what_is_already_the_latest_says_so_without_downloading_the_package()
    {
        await using var home = await StartAsync();
        var installer = Installer();
        Publish("1.0.0");
        var updates = Updates(installer, home.Questions, home.Time);
        await updates.InstallLatestAsync("sleipnir", default);
        _requests.Clear();

        var refused = await Assert.ThrowsAsync<ExtensionUpdates.UpdateException>(() => updates.InstallLatestAsync("sleipnir", default));

        Assert.Equal("up_to_date", refused.Code);
        Assert.DoesNotContain(_requests, r => r.EndsWith(".bifrost", StringComparison.Ordinal) || r.EndsWith(".sig", StringComparison.Ordinal)); // only the release listing was asked for
    }

    [Fact]
    public async Task An_update_is_offered_to_the_admin_once_and_only_applied_when_they_say_so()
    {
        await using var home = await StartAsync();
        var installer = Installer();
        Publish("1.0.0");
        await Updates(installer, home.Questions, home.Time).InstallLatestAsync("sleipnir", default);
        Publish("1.1.0");
        var applied = new TaskCompletionSource<string>();
        var updates = Updates(installer, home.Questions, home.Time, name => { applied.TrySetResult(name); return Task.CompletedTask; });

        var offers = await updates.OfferUpdatesAsync(default);

        var question = Assert.Single(home.Questions.ListOpen());
        Assert.Equal("bifrost", question.Asker);
        Assert.Contains("1.1.0", question.Text);
        Assert.Equal("1.1.0", Assert.Single(offers).Version);
        Assert.Equal("1.0.0", installer.CurrentVersion("sleipnir")); // offered, not applied
        Assert.Empty(await updates.OfferUpdatesAsync(default)); // and not asked again for the same version

        await home.Questions.AnswerAsync(question.Id, "update", home.Admin);
        Assert.Equal("sleipnir", await applied.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("1.1.0", installer.CurrentVersion("sleipnir"));
    }

    [Fact]
    public async Task Not_now_installs_nothing()
    {
        await using var home = await StartAsync();
        var installer = Installer();
        Publish("1.0.0");
        await Updates(installer, home.Questions, home.Time).InstallLatestAsync("sleipnir", default);
        Publish("1.1.0");
        await Updates(installer, home.Questions, home.Time).OfferUpdatesAsync(default);

        await home.Questions.AnswerAsync(home.Questions.ListOpen().Single().Id, "later", home.Admin);
        await Task.Delay(300);

        Assert.Equal("1.0.0", installer.CurrentVersion("sleipnir"));
    }

    [Fact]
    public async Task Nothing_is_offered_for_what_is_not_installed_or_already_current_and_offline_is_fine()
    {
        await using var home = await StartAsync();
        var installer = Installer();
        Publish("1.0.0");
        var updates = Updates(installer, home.Questions, home.Time);

        Assert.Empty(await updates.OfferUpdatesAsync(default)); // not installed: not kept up to date unasked
        await updates.InstallLatestAsync("sleipnir", default);
        Assert.Empty(await updates.OfferUpdatesAsync(default)); // already current
        _offline = true;
        Assert.Empty(await updates.OfferUpdatesAsync(default)); // no network: no error, no question
        Assert.Empty(home.Questions.ListOpen());
    }
}
