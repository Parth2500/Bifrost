using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Core.Api;

/// <summary>The 8-digit code both sides show when a device joins without a camera (design §7.2).</summary>
public static class JoinCode
{
    /// <summary>SHA-256 over the public key (SPKI): what a client pins and what the code is made from.</summary>
    public static byte[] FingerprintOf(byte[] subjectPublicKeyInfo) => SHA256.HashData(subjectPublicKeyInfo);

    public static string Commit(string deviceNonce) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceNonce)));

    public static string Compute(byte[] fingerprint, string deviceNonce, string serverNonce)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var hash = SHA256.HashData([.. fingerprint, .. Encoding.UTF8.GetBytes($"|{deviceNonce}|{serverNonce}")]);
        return (BitConverter.ToUInt32(hash, 0) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
    }
}
