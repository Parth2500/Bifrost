using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Core.Api;

namespace Bifrost.Client;

/// <summary>A paired device talking to its home: every call is pinned to the home key and carries the device token.</summary>
public sealed class BifrostClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private readonly HttpClient _http;

    public BifrostClient(PairedHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        Home = home;
        _http = PinnedHttp.Create(home.Fingerprint, home.Address);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", home.Token);
    }

    public PairedHome Home { get; }

    public Task<MeDto?> GetMeAsync(CancellationToken ct = default) => _http.GetFromJsonAsync<MeDto>("/api/v1/me", Json, ct);

    public Task<ExtensionDto[]?> GetExtensionsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<ExtensionDto[]>("/api/v1/extensions", Json, ct);

    /// <summary>
    /// Where an extension's screens are. A web view loads this with the token in an <c>Authorization</c>
    /// header (never in the address, never in a cookie), and must pin <see cref="PairedHome.Fingerprint"/> itself.
    /// </summary>
    public Uri ExtensionAddress(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Uri($"https://{Home.Address}/x/{Uri.EscapeDataString(name)}/");
    }

    /// <summary>A request the device may send to an extension's screens, already pinned and signed in.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> would leave the extension (another host, scheme or folder).</exception>
    public Task<HttpResponseMessage> GetFromExtensionAsync(string name, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = ExtensionAddress(name);
        var target = new Uri(root, path.TrimStart('/'));
        // The token travels with this request, so it may go nowhere but into the same extension on the same home.
        if (target.Scheme != Uri.UriSchemeHttps || target.Authority != root.Authority
            || !target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the extension.", nameof(path));
        }
        return _http.GetAsync(target, ct);
    }

    public void Dispose() => _http.Dispose();
}
