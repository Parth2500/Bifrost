using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bifrost.Core.Api;

namespace Bifrost.Client;

/// <summary>What a device keeps after pairing: where the home is, the key to pin, and its own token.</summary>
public sealed record PairedHome(string Address, string Fingerprint, string Token, Guid DeviceId);

/// <summary>Pairing from the device's side: path A with a link, path B with a compared code (design §7).</summary>
public static class BifrostPairing
{
    public sealed class PairingRefusedException(string message) : Exception(message);

    /// <summary>Path A: the secret goes only to a server whose key matches the link's fingerprint.</summary>
    public static async Task<PairedHome> PairWithLinkAsync(PairingLink link, string deviceName, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var http = PinnedHttp.Create(link.Fingerprint, link.Address);
        using var response = await SendAsync(() => http.PostAsJsonAsync("/api/v1/pair", new { secret = link.Secret, name = deviceName, platform }, ct));
        if (!response.IsSuccessStatusCode) throw new PairingRefusedException($"The home refused the invitation ({(int)response.StatusCode}).");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new PairedHome(link.Address, link.Fingerprint, body.GetProperty("token").GetString()!, body.GetProperty("deviceId").GetGuid());
    }

    /// <summary>
    /// Path B: ask to join, show <paramref name="showCode"/> to the person (who compares it with the admin's
    /// screen), then wait for the decision. Every call must meet the same key, or the join is dropped.
    /// </summary>
    public static async Task<PairedHome> JoinAsync(string address, string deviceName, string platform, Action<string> showCode,
        TimeSpan? pollEvery = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(showCode);
        using var http = PinnedHttp.Learn(address, out var seen);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var started = await PostAsync(http, "/api/v1/join", new { name = deviceName, platform, commit = JoinCode.Commit(nonce) }, ct);
        var id = started.GetProperty("requestId").GetGuid();
        var serverNonce = started.GetProperty("serverNonce").GetString()!;
        (await PostRawAsync(http, $"/api/v1/join/{id}/reveal", new { nonce }, ct)).Dispose();

        var fingerprint = OneKey(seen);
        showCode(JoinCode.Compute(Convert.FromHexString(fingerprint), nonce, serverNonce));

        while (true)
        {
            using var response = await PostRawAsync(http, $"/api/v1/join/{id}/collect", new { nonce }, ct, allow: [HttpStatusCode.Accepted, HttpStatusCode.Forbidden]);
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new PairingRefusedException("The admin refused this device.");
            OneKey(seen); // the certificate must not change between the steps
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                return new PairedHome(address, fingerprint, body.GetProperty("token").GetString()!, body.GetProperty("deviceId").GetGuid());
            }
            await Task.Delay(pollEvery ?? TimeSpan.FromSeconds(2), ct);
        }
    }

    private static string OneKey(PinnedHttp.PinnedHandler seen)
    {
        lock (seen.Seen)
        {
            return seen.Seen.Count == 1 ? seen.Seen.Single() : throw new PairingRefusedException("The home's key changed during pairing; nothing was paired.");
        }
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var response = await PostRawAsync(http, path, body, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient http, string path, object body, CancellationToken ct, HttpStatusCode[]? allow = null)
    {
        var response = await SendAsync(() => http.PostAsJsonAsync(path, body, ct));
        if (response.IsSuccessStatusCode || allow?.Contains(response.StatusCode) == true) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new PairingRefusedException($"The home answered {status} to {path}.");
    }

    /// <summary>A key that does not match is a failed handshake; say so in terms a person can act on.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try { return await send(); }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new PairingRefusedException("This is not the home the link names: its key does not match.");
        }
    }
}
