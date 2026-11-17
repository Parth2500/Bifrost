using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bifrost.Server.Security;

/// <summary>
/// The home key (design §6.2): an ECDSA P-256 key and a long-lived self-signed certificate, made on
/// first use and kept owner-only. Clients pin the <see cref="Fingerprint"/> (SHA-256 over the public
/// key), so renewing the certificate with the same key changes nothing for them.
/// </summary>
public sealed class HomeKey : IDisposable
{
    public const string FileName = "home-key.pfx";

    private HomeKey(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    /// <summary>Lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo.</summary>
    public string Fingerprint => FingerprintOf(Certificate);

    public static string FingerprintOf(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static HomeKey LoadOrCreate(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) Write(path, Create());
        return new HomeKey(X509CertificateLoader.LoadPkcs12FromFile(path, null));
    }

    /// <summary>A new key; every device pairs again (design §6.2).</summary>
    public static HomeKey Rotate(string folder)
    {
        var path = Path.Combine(folder, FileName);
        File.Delete(path);
        return LoadOrCreate(folder);
    }

    public void Dispose() => Certificate.Dispose();

    private static byte[] Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Bifrost {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return cert.Export(X509ContentType.Pfx);
    }

    private static void Write(string path, byte[] pfx)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // set before any content
        using var stream = new FileStream(path, options);
        stream.Write(pfx);
    }
}
