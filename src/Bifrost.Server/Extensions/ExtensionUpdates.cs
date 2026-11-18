using System.Text.Json;
using Bifrost.Core.Api;
using Bifrost.Server.Questions;

namespace Bifrost.Server.Extensions;

/// <summary>
/// Where releases come from (design §8.3): the latest release of each extension's own repository on GitHub, which is
/// downloaded with its signature and installed only if a carried key signed it. HTTPS to GitHub is not trusted for
/// anything but delivery; the signature is. An update is offered to the admin as a question and never applied silently.
/// </summary>
public sealed partial class ExtensionUpdates(ExtensionInstaller installer, QuestionService questions, HttpClient http, TimeProvider time,
    ILogger<ExtensionUpdates> log, Func<string, Task>? applied = null, string apiBase = "https://api.github.com")
{
    /// <summary>The owner's repositories, and only these: nothing else is ever downloaded. [my call: the repository names follow the apps' names.]</summary>
    public static readonly IReadOnlyDictionary<string, string> Repositories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sleipnir"] = "Parth2500/Sleipnir",
        ["saga"] = "Parth2500/Saga",
        ["norns"] = "Parth2500/Norns",
    };

    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private readonly Dictionary<string, string> _offered = new(StringComparer.Ordinal); // name → the version already asked about
    private readonly object _gate = new();

    public sealed record Latest(string Name, string Version, string PackageUrl, string SignatureUrl);

    public sealed class UpdateException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>The newest release of an extension that has a package and a signature, or null when there is none.</summary>
    public async Task<Latest?> LatestAsync(string name, CancellationToken ct)
    {
        if (!Repositories.TryGetValue(name, out var repo)) throw new UpdateException("no_source", "That extension has no repository to install from.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/repos/{repo}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Bifrost"); // GitHub asks for one; it names the program and nothing about this home
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        if (!response.IsSuccessStatusCode) throw new UpdateException("github_error", $"GitHub answered {(int)response.StatusCode}.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var version = json.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")) return null;
        string? package = null, signature = null;
        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            var assetName = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (assetName == $"{name}-{version}.bifrost") package = url;
            else if (assetName == $"{name}-{version}.bifrost.sig") signature = url;
        }
        return package is not null && signature is not null && IsHttps(package) && IsHttps(signature) ? new Latest(name, version, package, signature) : null;
    }

    /// <summary>Downloads and installs the newest release. The signature decides; nothing is installed without it.</summary>
    public async Task<ExtensionInstaller.Installed> InstallLatestAsync(string name, CancellationToken ct)
    {
        var latest = await LatestAsync(name, ct) ?? throw new UpdateException("no_release", "There is no signed release to install yet.");
        // Before any of the package is downloaded: the version is in the release's name.
        if (installer.CurrentVersion(name) is { } installed && ReleaseManifest.Compare(latest.Version, installed) <= 0)
        {
            throw new UpdateException("up_to_date", $"{name} {installed} is already installed, and it is the latest release.");
        }
        using var signatureResponse = await SendAsync(new HttpRequestMessage(HttpMethod.Get, latest.SignatureUrl), ct);
        signatureResponse.EnsureSuccessStatusCode();
        var signature = await signatureResponse.Content.ReadAsByteArrayAsync(ct);
        if (signature.Length != 64) throw new UpdateException("bad_signature", "The signature file is not 64 bytes.");

        using var packageResponse = await SendAsync(new HttpRequestMessage(HttpMethod.Get, latest.PackageUrl), ct, HttpCompletionOption.ResponseHeadersRead);
        packageResponse.EnsureSuccessStatusCode();
        if (packageResponse.Content.Headers.ContentLength > MaxPackageBytes) throw new UpdateException("too_large", "The package is larger than 512 MB.");
        await using var body = new LimitedStream(await packageResponse.Content.ReadAsStreamAsync(ct), MaxPackageBytes);
        return await installer.InstallAsync(body, signature, ct);
    }

    /// <summary>
    /// Looks for a newer release of each extension that is installed and asks the admin about it, once per version.
    /// </summary>
    /// <returns>The updates offered now.</returns>
    public async Task<IReadOnlyList<Latest>> OfferUpdatesAsync(CancellationToken ct)
    {
        var offers = new List<Latest>();
        foreach (var name in Repositories.Keys)
        {
            if (installer.CurrentVersion(name) is not { } current) continue; // only what is installed is kept up to date
            Latest? latest;
            try { latest = await LatestAsync(name, ct); }
            catch (Exception e) when (e is HttpRequestException or UpdateException or JsonException or TaskCanceledException or KeyNotFoundException)
            {
                LogCheckFailed(log, name, e.Message); // offline is normal: Bifröst needs no internet to run
                continue;
            }
            if (latest is null || ReleaseManifest.Compare(latest.Version, current) <= 0) continue;
            lock (_gate)
            {
                if (_offered.GetValueOrDefault(name) == latest.Version) continue;
                _offered[name] = latest.Version;
            }
            var question = questions.Ask(QuestionService.Self, $"Update {name}?", $"{name} {latest.Version} is available. You have {current}.",
                [new Choice("update", "Update"), new Choice("later", "Not now")], time.GetUtcNow().UtcDateTime.AddDays(1), online: false);
            offers.Add(latest);
            _ = Task.Run(() => ApplyWhenAnsweredAsync(question.Id, name, ct), ct);
        }
        return offers;
    }

    private async Task ApplyWhenAnsweredAsync(Guid id, string name, CancellationToken ct)
    {
        try
        {
            QuestionDto? closed;
            do { closed = await questions.WaitUntilClosedAsync(id, QuestionService.MaxWait, ct); }
            while (closed is { State: QuestionState.Open });
            if (closed is not { State: QuestionState.Answered, Choice: "update" }) return;
            var done = await InstallLatestAsync(name, ct);
            LogUpdated(log, done.Name, done.Version);
            if (applied is not null) await applied(name);
        }
        catch (Exception e) when (e is HttpRequestException or UpdateException or ExtensionInstaller.InstallRefusedException or OperationCanceledException or IOException)
        {
            LogUpdateFailed(log, name, e.Message);
            lock (_gate) _offered.Remove(name); // so the next check can offer it again
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        if (!IsHttps(request.RequestUri!.ToString())) throw new UpdateException("not_https", "Releases are only fetched over HTTPS.");
        return http.SendAsync(request, option, ct);
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Stops reading at a limit, so a server that never stops cannot fill the disk.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            _read += n;
            return _read > limit ? throw new UpdateException("too_large", "The package is larger than 512 MB.") : n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not check {Name} for updates: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated {Name} to {Version}")]
    private static partial void LogUpdated(ILogger logger, string name, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update {Name}: {Reason}")]
    private static partial void LogUpdateFailed(ILogger logger, string name, string reason);
}
