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
    public Uri ExtensionAddress(string name) => AddressOf(Area.Extension(name));

    public Uri AddressOf(Area area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return new Uri($"https://{Home.Address}{area.Root}");
    }

    /// <summary>A request the device may send to an extension's screens, already pinned and signed in.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> would leave the extension (another host, scheme or folder).</exception>
    public Task<HttpResponseMessage> GetFromExtensionAsync(string name, string path, CancellationToken ct = default) =>
        _http.GetAsync(Resolve(Area.Extension(name), path), ct);

    /// <summary>
    /// Where a path leads, if it stays inside the area. A path starting with <c>/</c> is from the home's root,
    /// any other from the area's own folder. The token travels with the request, so it may go nowhere else.
    /// </summary>
    private Uri Resolve(Area area, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = AddressOf(area);
        // A dot segment, spelled plainly or percent-encoded (%2e%2e), would climb out of the area.
        var beforeQuery = path.Split('?', '#')[0];
        if (Uri.UnescapeDataString(beforeQuery).Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The path leaves the area.", nameof(path));
        }
        var target = new Uri(root, path);
        if (!Inside(area, root, target) || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the area.", nameof(path));
        }
        return target;
    }

    private static bool Inside(Area area, Uri root, Uri target) =>
        target.Scheme == Uri.UriSchemeHttps && target.Authority == root.Authority
        && (target.AbsolutePath.StartsWith(area.Root, StringComparison.Ordinal)
            || area.AlsoUnder.Any(prefix => target.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>
    /// A request with a body for a page that cannot send one itself (a web view hands the app only GETs).
    /// Only POST and DELETE, only inside the area, a small JSON body at most.
    /// </summary>
    /// <exception cref="ArgumentException">A method or path that is not allowed.</exception>
    public Task<(int Status, string Body)> SendToExtensionAsync(string name, string method, string path, string? body, CancellationToken ct = default) =>
        SendAsync(Area.Extension(name), method, path, body, ct);

    public async Task<(int Status, string Body)> SendAsync(Area area, string method, string path, string? body, CancellationToken ct = default)
    {
        if (method is not ("POST" or "DELETE")) throw new ArgumentException("Only POST and DELETE.", nameof(method));
        if (body is { Length: > 65536 }) throw new ArgumentException("The body is too large.", nameof(body));
        using var request = new HttpRequestMessage(new HttpMethod(method), Resolve(area, path));
        if (!string.IsNullOrEmpty(body)) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Signs this device out on the home, so its token stops working. True when it no longer works there.</summary>
    public async Task<bool> RevokeSelfAsync(CancellationToken ct = default)
    {
        var (status, _) = await SendAsync(Area.HomePages, "DELETE", $"/api/v1/devices/{Home.DeviceId}", null, ct);
        // Signed out now, or already: the home no longer knows this token (401) or this device (404).
        return status is (>= 200 and < 300) or 401 or 404;
    }

    public Task<HttpResponseMessage> GetHomeAsync(string extension, Uri address, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        GetHomeAsync(Area.Extension(extension), address, headers, ct);

    /// <summary>
    /// For a web view that hands every request to the app: GET one address inside <paramref name="area"/> on this
    /// home, pinned and signed in. Anything else (another host, scheme or folder) is refused before it is sent.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not inside that area.</exception>
    public Task<HttpResponseMessage> GetHomeAsync(Area area, Uri address, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(area);
        if (!Inside(area, AddressOf(area), address) || address.AbsolutePath.Split('/').Any(segment => Uri.UnescapeDataString(segment) is "." or ".."))
        {
            throw new ArgumentException("Not a screen of this area.", nameof(address));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            // The person's own headers help the page; the token and the host are never theirs to set.
            if (!name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public void Dispose() => _http.Dispose();
}
