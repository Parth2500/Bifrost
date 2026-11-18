using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Server.Extensions;

namespace Bifrost.Tests.Extensions;

public sealed class ExtensionInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bifrost-install-").FullName;
    private readonly ECDsa _owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _backup = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Spki(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private ExtensionInstaller Installer(params ECDsa[] trusted) => new(_root, new ReleaseKeys(trusted.Select(Spki)));

    private static string Manifest(string name = "sleipnir", string version = "1.0.0", string interfaceRange = """{"min":1,"max":1}""",
        string program = "bin/sleipnir") => $$"""
        {"name":"{{name}}","version":"{{version}}","interface":{{interfaceRange}},"program":{"linux-x64":"{{program}}","linux-arm64":"{{program}}"},
         "emits":["download.finished"],"wants":[],"browser-fallback":{"accepts":true,"routes":["/x/sleipnir/"]},"folders":[{"name":"downloads","write":true}]}
        """;

    private static byte[] Package(string manifest, Action<ZipArchive>? extra = null, bool withProgram = true)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(manifest);
            if (withProgram) using (var w = new StreamWriter(zip.CreateEntry("bin/sleipnir").Open())) w.Write("#!/bin/sh\necho hi\n");
            extra?.Invoke(zip);
        }
        return memory.ToArray();
    }

    private static byte[] Sign(ECDsa key, byte[] package) =>
        key.SignHash(SHA256.HashData(package), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static Task<ExtensionInstaller.Installed> Install(ExtensionInstaller installer, byte[] package, byte[] signature) =>
        installer.InstallAsync(new MemoryStream(package), signature);

    private static async Task<string> RefusedAsync(Task<ExtensionInstaller.Installed> install) =>
        (await Assert.ThrowsAsync<ExtensionInstaller.InstallRefusedException>(() => install)).Code;

    [Fact]
    public async Task A_signed_package_is_unpacked_and_made_current()
    {
        var package = Package(Manifest());

        var installed = await Install(Installer(_owner), package, Sign(_owner, package));

        Assert.Equal(("sleipnir", "1.0.0", null), (installed.Name, installed.Version, installed.Previous));
        Assert.True(File.Exists(Path.Combine(_root, "sleipnir", "current", "bin", "sleipnir")));
        Assert.Equal("1.0.0", Installer(_owner).CurrentVersion("sleipnir"));
        Assert.Equal(Path.Combine(_root, "sleipnir", "current", "bin", "sleipnir"), Installer(_owner).Programs()["sleipnir"]);
        Assert.True(File.GetUnixFileMode(Path.Combine(_root, "sleipnir", "current", "bin", "sleipnir")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task A_package_signed_by_a_stranger_or_changed_after_signing_installs_nothing()
    {
        var package = Package(Manifest());
        var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Equal("bad_signature", await RefusedAsync(Install(Installer(_owner), package, Sign(stranger, package))));
        var tampered = Package(Manifest(), z => { using var w = new StreamWriter(z.CreateEntry("extra").Open()); w.Write("x"); });
        Assert.Equal("bad_signature", await RefusedAsync(Install(Installer(_owner), tampered, Sign(_owner, package))));
        Assert.Equal("bad_signature", await RefusedAsync(Install(Installer(_owner), package, new byte[10])));
        Assert.False(Directory.Exists(Path.Combine(_root, "sleipnir")));
        Assert.Empty(Directory.GetFiles(_root)); // the spooled copy is gone too
    }

    [Fact]
    public async Task Either_of_the_two_carried_keys_is_enough()
    {
        var package = Package(Manifest());

        await Install(Installer(_owner, _backup), package, Sign(_backup, package));

        Assert.Equal("1.0.0", Installer(_owner).CurrentVersion("sleipnir"));
    }

    [Fact]
    public async Task With_no_keys_built_in_nothing_can_be_installed()
    {
        var package = Package(Manifest());

        Assert.Equal("no_keys", await RefusedAsync(Install(new ExtensionInstaller(_root, new ReleaseKeys([])), package, Sign(_owner, package))));
    }

    [Fact]
    public async Task A_name_that_leaves_the_folder_is_refused()
    {
        var package = Package(Manifest(), z => { using var w = new StreamWriter(z.CreateEntry("../../evil").Open()); w.Write("x"); });

        Assert.Equal("bad_package", await RefusedAsync(Install(Installer(_owner), package, Sign(_owner, package))));
        Assert.False(File.Exists(Path.Combine(_root, "evil")));
        Assert.False(Directory.Exists(Path.Combine(_root, "sleipnir", "current")));
    }

    [Fact]
    public async Task A_link_inside_the_package_is_refused()
    {
        var package = Package(Manifest(), z =>
        {
            var link = z.CreateEntry("bin/link");
            link.ExternalAttributes = (0xA000 | 0x1FF) << 16; // a symbolic link
            using var w = new StreamWriter(link.Open());
            w.Write("/etc/passwd");
        });

        Assert.Equal("bad_package", await RefusedAsync(Install(Installer(_owner), package, Sign(_owner, package))));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"name":"nope","version":"1.0.0","program":{"linux-x64":"bin/sleipnir"}}""")] // not a known extension
    [InlineData("""{"name":"sleipnir","version":"banana","program":{"linux-x64":"bin/sleipnir"}}""")]
    [InlineData("""{"name":"sleipnir","version":"1.0.0","interface":{"min":2,"max":3},"program":{"linux-x64":"bin/sleipnir"}}""")] // needs a newer interface
    [InlineData("""{"name":"sleipnir","version":"1.0.0","program":{"linux-x64":"../../bin/sh","linux-arm64":"../../bin/sh"}}""")]
    [InlineData("""{"name":"sleipnir","version":"1.0.0","program":{"linux-x64":"bin/missing","linux-arm64":"bin/missing"}}""")] // not in the package
    public async Task A_manifest_that_does_not_hold_up_is_refused(string manifest)
    {
        var package = Package(manifest);

        Assert.Equal("bad_manifest", await RefusedAsync(Install(Installer(_owner), package, Sign(_owner, package))));
    }

    [Fact]
    public async Task An_update_keeps_the_version_before_and_an_old_release_cannot_be_replayed()
    {
        var installer = Installer(_owner);
        var one = Package(Manifest(version: "1.0.0"));
        var two = Package(Manifest(version: "1.1.0"));
        await Install(installer, one, Sign(_owner, one));

        var updated = await Install(installer, two, Sign(_owner, two));

        Assert.Equal("1.0.0", updated.Previous);
        Assert.Equal("1.1.0", installer.CurrentVersion("sleipnir"));
        Assert.True(Directory.Exists(Path.Combine(_root, "sleipnir", "versions", "1.0.0")));
        Assert.Equal("not_newer", await RefusedAsync(Install(installer, one, Sign(_owner, one)))); // properly signed, but old
        Assert.Equal("not_newer", await RefusedAsync(Install(installer, two, Sign(_owner, two)))); // and not the same either
    }

    [Fact]
    public async Task Rollback_goes_back_and_a_second_rollback_goes_forward_again()
    {
        var installer = Installer(_owner);
        foreach (var v in new[] { "1.0.0", "1.1.0" })
        {
            var p = Package(Manifest(version: v));
            await Install(installer, p, Sign(_owner, p));
        }

        Assert.Equal("1.0.0", installer.Rollback("sleipnir").Version);
        Assert.Equal("1.0.0", installer.CurrentVersion("sleipnir"));
        Assert.Equal("1.1.0", installer.Rollback("sleipnir").Version);
        Assert.Equal("nothing_to_roll_back", Assert.Throws<ExtensionInstaller.InstallRefusedException>(() => installer.Rollback("saga")).Code);
    }

    [Fact]
    public async Task Only_the_current_and_the_previous_version_are_kept()
    {
        var installer = Installer(_owner);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            var p = Package(Manifest(version: v));
            await Install(installer, p, Sign(_owner, p));
        }

        var kept = Directory.GetDirectories(Path.Combine(_root, "sleipnir", "versions")).Select(d => Path.GetFileName(d)!).Order().ToArray();
        Assert.Equal(["1.1.0", "1.2.0"], kept);
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused_after_its_signature_checks()
    {
        var junk = Encoding.UTF8.GetBytes("this is not a zip");

        Assert.Equal("bad_package", await RefusedAsync(Install(Installer(_owner), junk, Sign(_owner, junk))));
    }
}
