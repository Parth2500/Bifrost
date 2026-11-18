using System.Security.Cryptography;

namespace Bifrost.Server.Extensions;

/// <summary>
/// The public keys a release must be signed by (design §8.3): ECDSA P-256 over the SHA-256 of the package, the
/// signature as 64 bytes (r then s). Two keys are carried, the one in use and a backup kept apart, and a release
/// signed by either is accepted. The private keys stay offline, with the owner. [my call: ECDSA P-256, because the
/// platform has it; the design left the scheme to the builder.]
/// </summary>
public sealed class ReleaseKeys
{
    /// <summary>
    /// The keys built into this Bifröst, as base64 SubjectPublicKeyInfo (<c>bifrost release keygen</c> makes a pair and
    /// prints this text). The first is the key in use; the second is the backup, kept somewhere separate, so a lost
    /// first key does not end all updates. With none, every install is refused.
    /// </summary>
    public static readonly string[] BuiltIn =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE38ry0jNDkh7eNLTeZ4nkSvSrAJsfGvzYzNCNqMqPp6lwVJeBp6eROYeuCDWDUK+3qcfLMjQMqIzIA1WiO+eNFQ==", // owner, 2026-11-18
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEFBnpoRsPM+PUvWR7qAb2NaBh+pT8fBogXk03GiznZL3Xjk9MUX7ehS2JQhCxPZfgn6l9+wfTZqwqizUQviQxw==", // backup, 2026-11-18
    ];

    private readonly List<ECDsa> _keys = [];

    public ReleaseKeys(IEnumerable<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        foreach (var text in publicKeys)
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text), out _);
            _keys.Add(key);
        }
    }

    public int Count => _keys.Count;

    public static ReleaseKeys Default() => new(BuiltIn);

    /// <summary>True when any carried key made this signature over the hash of the package.</summary>
    public bool Verify(byte[] packageSha256, byte[] signature) =>
        signature.Length == 64 && _keys.Any(k => k.VerifyHash(packageSha256, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
