using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bifrost.Server.Extensions;

/// <summary>
/// What the owner runs on their own machine to publish an extension (design §8.3): make a key pair, pack a release
/// into the zip that <see cref="ExtensionInstaller"/> reads, sign it, and check a signature. Nothing here needs the
/// network, and the private key is written to a file only its owner can read and never goes anywhere else.
/// </summary>
public static class ReleaseBuilder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // the same inputs give the same package

    /// <summary>Writes <c>&lt;name&gt;.key</c> (private, PEM, mode 0600) and <c>&lt;name&gt;.pub</c> (the public key as base64 SPKI).</summary>
    /// <returns>The public key text, to paste into <see cref="ReleaseKeys.BuiltIn"/>.</returns>
    public static string WriteKeyPair(string folder, string name)
    {
        var keyPath = Path.Combine(folder, name + ".key");
        var pubPath = Path.Combine(folder, name + ".pub");
        if (File.Exists(keyPath) || File.Exists(pubPath)) throw new IOException($"{keyPath} or {pubPath} already exists. A key is never overwritten.");
        Directory.CreateDirectory(folder);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Created with the owner-only mode from the start, so the key is never readable by anyone for a moment.
        using (var stream = new FileStream(keyPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        }
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(pubPath, pub + "\n");
        return pub;
    }

    /// <param name="programs">Platform (such as <c>linux-x64</c>) → the local file to ship for it.</param>
    /// <param name="folders">Folder name → whether the extension writes to it.</param>
    /// <exception cref="ReleaseManifest.InvalidManifestException">The name or version is not acceptable, or a program file is missing.</exception>
    public static void Pack(string outPath, string name, string version, IReadOnlyDictionary<string, string> programs,
        IEnumerable<string>? emits = null, IEnumerable<string>? wants = null, IReadOnlyDictionary<string, bool>? folders = null)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var inside = programs.ToDictionary(p => p.Key, p => $"bin/{p.Key}/{Path.GetFileName(p.Value)}");
        var manifest = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["version"] = version,
            ["interface"] = new { min = ReleaseManifest.InterfaceVersion, max = ReleaseManifest.InterfaceVersion },
            ["program"] = inside,
            ["emits"] = emits ?? [],
            ["wants"] = wants ?? [],
            ["folders"] = (folders ?? new Dictionary<string, bool>()).Select(f => new { name = f.Key, write = f.Value }),
        }, Indented);
        ReleaseManifest.Parse(manifest); // the same checks the installer will make, before a package exists
        foreach (var file in programs.Values)
        {
            if (!File.Exists(file)) throw new ReleaseManifest.InvalidManifestException($"No such file: {file}");
        }

        File.Delete(outPath); // a build output: packing again replaces it
        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        Add(zip, "manifest.json", System.Text.Encoding.UTF8.GetBytes(manifest + "\n"), executable: false);
        foreach (var (platform, file) in programs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Add(zip, inside[platform], File.ReadAllBytes(file), executable: true);
        }
    }

    private static void Add(ZipArchive zip, string path, byte[] bytes, bool executable)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTime;
        entry.ExternalAttributes = (executable ? 0x81ED : 0x81A4) << 16; // a regular file, 0755 or 0644
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <returns>The 64-byte signature (r then s) over the SHA-256 of the package file.</returns>
    public static byte[] Sign(string privateKeyPem, string packagePath)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return key.SignHash(HashOf(packagePath), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static bool Verify(string publicKeyBase64, string packagePath, byte[] signature) =>
        new ReleaseKeys([publicKeyBase64]).Verify(HashOf(packagePath), signature);

    private static byte[] HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
