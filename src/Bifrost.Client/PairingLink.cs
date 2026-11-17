using System.Web;

namespace Bifrost.Client;

/// <summary>The invitation link <c>bifrost://pair?h=&lt;address:port&gt;&amp;fp=&lt;fingerprint&gt;&amp;t=&lt;secret&gt;&amp;n=&lt;home name&gt;</c> (design §7.1).</summary>
public sealed record PairingLink(string Address, string Fingerprint, string Secret, string HomeName)
{
    /// <exception cref="FormatException">Not a Bifröst pairing link, or a part is missing or malformed.</exception>
    public static PairingLink Parse(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "bifrost" || uri.Host != "pair")
        {
            throw new FormatException("Not a Bifröst pairing link.");
        }
        var query = HttpUtility.ParseQueryString(uri.Query);
        string Part(string key) => query[key] is { Length: > 0 } v ? v : throw new FormatException($"The link has no '{key}'.");
        var fingerprint = Part("fp").ToLowerInvariant();
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new FormatException("The link's fingerprint is not 64 hex characters.");
        return new PairingLink(Part("h"), fingerprint, Part("t"), query["n"] ?? "");
    }
}
