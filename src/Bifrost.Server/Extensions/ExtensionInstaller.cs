using System.IO.Compression;
using System.Security.Cryptography;

namespace Bifrost.Server.Extensions;

/// <summary>
/// Installs an extension from a signed package (design §8.3). The package is checked against the carried release
/// keys <i>before</i> anything in it is read as anything but bytes; it is then unpacked into a folder per version
/// and a <c>current</c> link is switched to it. The version before stays, so it can be rolled back to.
/// <code>
/// &lt;root&gt;/&lt;name&gt;/versions/&lt;version&gt;/…   &lt;root&gt;/&lt;name&gt;/current → versions/&lt;version&gt;   &lt;root&gt;/&lt;name&gt;/previous (text)
/// </code>
/// </summary>
public sealed class ExtensionInstaller(string root, ReleaseKeys keys)
{
    private const int MaxEntries = 20_000;
    private const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public sealed class InstallRefusedException(string code, string message) : Exception(message)
    {
        /// <summary>A stable word for the API and the audit log: <c>bad_signature</c>, <c>bad_package</c>, <c>not_newer</c>…</summary>
        public string Code { get; } = code;
    }

    public sealed record Installed(string Name, string Version, string? Previous, string Program);

    /// <summary>The program an installed extension runs, through the <c>current</c> link; null when nothing is installed.</summary>
    public string? ProgramFor(string name)
    {
        var current = Path.Combine(root, name, "current");
        if (!Directory.Exists(current) || !File.Exists(Path.Combine(root, name, "program"))) return null;
        return Path.Combine(current, File.ReadAllText(Path.Combine(root, name, "program")).Trim());
    }

    /// <summary>Every installed extension and the program it runs, for the daemon to start.</summary>
    public IReadOnlyDictionary<string, string> Programs() =>
        ExtensionCatalog.Names.Select(n => (n, p: ProgramFor(n))).Where(x => x.p is not null).ToDictionary(x => x.n, x => x.p!);

    public string? CurrentVersion(string name) => VersionOf(Path.Combine(root, name, "current"));

    /// <param name="package">The package, read once.</param>
    /// <param name="signature">64 bytes: ECDSA P-256 (r then s) over the SHA-256 of the package.</param>
    public async Task<Installed> InstallAsync(Stream package, byte[] signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(signature);
        if (keys.Count == 0) throw new InstallRefusedException("no_keys", "No release keys are built into this Bifröst, so nothing can be installed.");
        Directory.CreateDirectory(root, OwnerOnly);
        var incoming = Path.Combine(root, ".incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Spooled to disk and hashed as it arrives: a package can be large, and nothing is read until it is trusted.
            byte[] hash;
            await using (var file = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await package.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                hash = sha.GetHashAndReset();
            }
            if (!keys.Verify(hash, signature)) throw new InstallRefusedException("bad_signature", "The package was not signed by a key this Bifröst trusts. Nothing was installed.");
            return Unpack(incoming);
        }
        finally
        {
            File.Delete(incoming);
        }
    }

    /// <summary>Goes back to the version before. Another rollback goes forward again.</summary>
    public Installed Rollback(string name)
    {
        var folder = Path.Combine(root, name);
        var previousFile = Path.Combine(folder, "previous");
        if (!ExtensionCatalog.Names.Contains(name) || !File.Exists(previousFile)) throw new InstallRefusedException("nothing_to_roll_back", "There is no earlier version to go back to.");
        var previous = File.ReadAllText(previousFile).Trim();
        var current = CurrentVersion(name);
        if (!Directory.Exists(Path.Combine(folder, "versions", previous))) throw new InstallRefusedException("nothing_to_roll_back", "The earlier version is gone.");
        Switch(folder, previous);
        File.WriteAllText(previousFile, current ?? "");
        return new Installed(name, previous, current, ProgramFor(name)!);
    }

    private Installed Unpack(string packagePath)
    {
        using var zip = OpenZip(packagePath);
        if (zip.Entries.Count > MaxEntries) throw new InstallRefusedException("bad_package", "The package has too many files.");
        if (zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes) throw new InstallRefusedException("bad_package", "The package is too large when unpacked.");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InstallRefusedException("bad_package", "The package has no manifest.json.");
        if (manifestEntry.Length > 64 * 1024) throw new InstallRefusedException("bad_package", "manifest.json is too large.");
        ReleaseManifest manifest;
        string program;
        try
        {
            using var reader = new StreamReader(manifestEntry.Open());
            manifest = ReleaseManifest.Parse(reader.ReadToEnd());
            program = manifest.ProgramHere;
        }
        catch (ReleaseManifest.InvalidManifestException e)
        {
            throw new InstallRefusedException("bad_manifest", e.Message);
        }
        if (zip.GetEntry(program) is null) throw new InstallRefusedException("bad_manifest", $"The program {program} is not in the package.");

        var folder = Path.Combine(root, manifest.Name);
        Directory.CreateDirectory(Path.Combine(folder, "versions"), OwnerOnly);
        var current = CurrentVersion(manifest.Name);
        if (current is not null && ReleaseManifest.Compare(manifest.Version, current) <= 0)
        {
            // A signed old release must not be replayed to put a known flaw back; Rollback is the way to an earlier one.
            throw new InstallRefusedException("not_newer", $"Version {manifest.Version} is not newer than the installed {current}.");
        }

        var target = Path.Combine(folder, "versions", manifest.Version);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // left by an earlier, rolled-back install
        var staging = target + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        try
        {
            ExtractSafely(zip, staging);
            var programFile = Path.Combine(staging, program);
            File.SetUnixFileMode(programFile, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        File.WriteAllText(Path.Combine(folder, "program"), program);
        Switch(folder, manifest.Version);
        File.WriteAllText(Path.Combine(folder, "previous"), current ?? "");
        // Only the version now running and the one before are kept.
        foreach (var old in Directory.GetDirectories(Path.Combine(folder, "versions")).Where(d => Path.GetFileName(d) != manifest.Version && Path.GetFileName(d) != current))
        {
            Directory.Delete(old, recursive: true);
        }
        return new Installed(manifest.Name, manifest.Version, current, Path.Combine(folder, "current", program));
    }

    /// <summary>The link is replaced in one step, so a reader sees the old version or the new one, never neither.</summary>
    private static void Switch(string folder, string version)
    {
        var link = Path.Combine(folder, "current");
        var next = link + ".new";
        File.Delete(next);
        File.CreateSymbolicLink(next, Path.Combine("versions", version));
        // rename(2) swaps a link for a link in one step; File.Move looks through the link and finds a folder, not a file.
        if (Rename(Utf8Z(next), Utf8Z(link)) != 0) throw new IOException($"Could not switch {link}: errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}.");
    }

    // Paths go over as null-terminated UTF-8 bytes, so there is no string marshalling to get wrong.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    private static extern int Rename(byte[] from, byte[] to);

    private static byte[] Utf8Z(string path) => [.. System.Text.Encoding.UTF8.GetBytes(path), 0];

    private static string? VersionOf(string link) =>
        Directory.Exists(link) && new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.Name : null;

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new InstallRefusedException("bad_package", "The package is not a zip file."); }
    }

    /// <summary>Refuses a name that leaves the folder, an absolute name, a link, and a name that repeats.</summary>
    private static void ExtractSafely(ZipArchive zip, string destination)
    {
        Directory.CreateDirectory(destination, OwnerOnly);
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var isFolder = name.EndsWith('/');
            var clean = isFolder ? name[..^1] : name;
            if (clean.Length == 0 || !ReleaseManifest.SafeRelative(clean)) throw new InstallRefusedException("bad_package", $"The package has an unsafe path: {name}");
            // The top byte of the attributes is the Unix file type: a link (0xA000) would point anywhere.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InstallRefusedException("bad_package", $"The package has a link: {name}");
            var path = Path.GetFullPath(Path.Combine(destination, clean));
            if (!path.StartsWith(full, StringComparison.Ordinal)) throw new InstallRefusedException("bad_package", $"The package has an unsafe path: {name}");
            if (!seen.Add(clean)) throw new InstallRefusedException("bad_package", $"The package lists {name} twice.");
            if (isFolder)
            {
                Directory.CreateDirectory(path, OwnerOnly);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, OwnerOnly);
            entry.ExtractToFile(path, overwrite: false);
        }
    }
}
