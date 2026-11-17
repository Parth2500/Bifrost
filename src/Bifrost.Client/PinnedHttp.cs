using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Bifrost.Core.Api;

namespace Bifrost.Client;

/// <summary>
/// HTTP that trusts one key. The server's certificate is self-signed, so no chain is checked; what
/// counts is that its public key hashes to the pinned fingerprint (design §6.2). The address and the
/// name do not matter, so a new IP address changes nothing.
/// </summary>
public static class PinnedHttp
{
    /// <param name="pinned">Lowercase hex SHA-256 of the key.</param>
    public static HttpClient Create(string pinned, string address) => Create(pinned, address, out _);

    public static HttpClient Create(string pinned, string address, out PinnedHandler handler)
    {
        handler = new PinnedHandler(pinned);
        return new HttpClient(handler.Inner) { BaseAddress = new Uri($"https://{address}") };
    }

    /// <summary>Holds the pin and the fingerprint last seen. <c>pinned</c> null means "learn it": the first join step.</summary>
    public sealed class PinnedHandler
    {
        private readonly string? _pinned;

        public PinnedHandler(string? pinned)
        {
            _pinned = pinned;
            Inner = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
            };
        }

        public SocketsHttpHandler Inner { get; }

        /// <summary>Every fingerprint this handler has seen; more than one means the certificate changed under us.</summary>
        public HashSet<string> Seen { get; } = [];

        private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null) return false;
            using var cert = new X509Certificate2(certificate);
            var seen = Convert.ToHexStringLower(JoinCode.FingerprintOf(cert.PublicKey.ExportSubjectPublicKeyInfo()));
            lock (Seen) Seen.Add(seen);
            return _pinned is null || string.Equals(seen, _pinned, StringComparison.Ordinal);
        }
    }

    /// <summary>For the first step of a join, which trusts what it sees for this request only and reports it.</summary>
    public static HttpClient Learn(string address, out PinnedHandler handler)
    {
        handler = new PinnedHandler(null);
        return new HttpClient(handler.Inner) { BaseAddress = new Uri($"https://{address}") };
    }
}
