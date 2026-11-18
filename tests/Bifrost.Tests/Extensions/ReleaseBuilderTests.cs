using System.IO.Compression;
using Bifrost.Server.Extensions;

namespace Bifrost.Tests.Extensions;

public sealed class ReleaseBuilderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bifrost-release-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Program()
    {
        var path = Path.Combine(_dir, "sleipnir");
        File.WriteAllText(path, "#!/bin/sh\necho hi\n");
        return path;
    }

    private static Dictionary<string, string> Both(string file) => new() { ["linux-x64"] = file, ["linux-arm64"] = file };

    [Fact]
    public void The_key_pair_is_written_once_with_the_private_half_readable_only_by_its_owner()
    {
        var pub = ReleaseBuilder.WriteKeyPair(_dir, "owner");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "owner.key")));
        Assert.Equal(pub, File.ReadAllText(Path.Combine(_dir, "owner.pub")).Trim());
        Assert.Throws<IOException>(() => ReleaseBuilder.WriteKeyPair(_dir, "owner")); // never overwritten
    }

    [Fact]
    public async Task What_the_owner_packs_and_signs_is_what_Bifrost_installs()
    {
        var pub = ReleaseBuilder.WriteKeyPair(_dir, "owner");
        var package = Path.Combine(_dir, "sleipnir-0.11.19.bifrost");
        ReleaseBuilder.Pack(package, "sleipnir", "0.11.19", Both(Program()), emits: ["download.finished"], folders: new Dictionary<string, bool> { ["downloads"] = true });
        var signature = ReleaseBuilder.Sign(File.ReadAllText(Path.Combine(_dir, "owner.key")), package);
        var root = Path.Combine(_dir, "installed");

        var installed = await new ExtensionInstaller(root, new ReleaseKeys([pub])).InstallAsync(File.OpenRead(package), signature);

        Assert.Equal(("sleipnir", "0.11.19"), (installed.Name, installed.Version));
        Assert.True(File.Exists(installed.Program));
        Assert.True(ReleaseBuilder.Verify(pub, package, signature));
    }

    [Fact]
    public void Another_key_does_not_verify_and_a_changed_package_does_not_either()
    {
        var pub = ReleaseBuilder.WriteKeyPair(_dir, "owner");
        var other = ReleaseBuilder.WriteKeyPair(_dir, "other");
        var package = Path.Combine(_dir, "p.bifrost");
        ReleaseBuilder.Pack(package, "sleipnir", "1.0.0", Both(Program()));
        var signature = ReleaseBuilder.Sign(File.ReadAllText(Path.Combine(_dir, "owner.key")), package);

        Assert.False(ReleaseBuilder.Verify(other, package, signature));
        File.AppendAllText(package, "x");
        Assert.False(ReleaseBuilder.Verify(pub, package, signature));
    }

    [Fact]
    public void The_same_inputs_give_the_same_package()
    {
        var one = Path.Combine(_dir, "a.bifrost");
        var two = Path.Combine(_dir, "b.bifrost");
        var program = Program();

        ReleaseBuilder.Pack(one, "sleipnir", "1.0.0", Both(program));
        ReleaseBuilder.Pack(two, "sleipnir", "1.0.0", Both(program));

        Assert.Equal(File.ReadAllBytes(one), File.ReadAllBytes(two));
    }

    [Theory]
    [InlineData("nope", "1.0.0")] // not one of the owner's extensions
    [InlineData("sleipnir", "v1")] // not a version
    public void A_package_the_installer_would_refuse_is_not_made(string name, string version)
    {
        Assert.Throws<ReleaseManifest.InvalidManifestException>(() =>
            ReleaseBuilder.Pack(Path.Combine(_dir, "x.bifrost"), name, version, Both(Program())));
        Assert.False(File.Exists(Path.Combine(_dir, "x.bifrost")));
    }

    [Fact]
    public void A_missing_program_file_is_named_and_the_package_has_a_manifest_and_the_program()
    {
        Assert.Throws<ReleaseManifest.InvalidManifestException>(() =>
            ReleaseBuilder.Pack(Path.Combine(_dir, "x.bifrost"), "sleipnir", "1.0.0", Both(Path.Combine(_dir, "missing"))));

        var package = Path.Combine(_dir, "ok.bifrost");
        ReleaseBuilder.Pack(package, "sleipnir", "1.0.0", Both(Program()));
        using var zip = ZipFile.OpenRead(package);
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName == "bin/linux-x64/sleipnir");
    }
}

public sealed class BuiltInKeyTests
{
    [Fact]
    public void Every_built_in_key_is_a_valid_P256_public_key_and_there_is_at_least_one()
    {
        Assert.NotEmpty(ReleaseKeys.BuiltIn);
        var keys = ReleaseKeys.Default(); // throws if any is not a P-256 SubjectPublicKeyInfo
        Assert.Equal(ReleaseKeys.BuiltIn.Length, keys.Count);
        Assert.Equal(ReleaseKeys.BuiltIn.Length, ReleaseKeys.BuiltIn.Distinct().Count());
    }

    [Fact]
    public async Task A_package_signed_by_anyone_but_the_owner_is_refused_by_the_real_key_list()
    {
        var dir = Directory.CreateTempSubdirectory("bifrost-builtin-").FullName;
        var stranger = ReleaseBuilder.WriteKeyPair(dir, "stranger");
        var program = Path.Combine(dir, "sleipnir");
        File.WriteAllText(program, "#!/bin/sh\n");
        var package = Path.Combine(dir, "p.bifrost");
        ReleaseBuilder.Pack(package, "sleipnir", "1.0.0", new Dictionary<string, string> { ["linux-x64"] = program, ["linux-arm64"] = program });
        var signature = ReleaseBuilder.Sign(File.ReadAllText(Path.Combine(dir, "stranger.key")), package);

        var refused = await Assert.ThrowsAsync<ExtensionInstaller.InstallRefusedException>(() =>
            new ExtensionInstaller(Path.Combine(dir, "installed"), ReleaseKeys.Default()).InstallAsync(File.OpenRead(package), signature));

        Assert.Equal("bad_signature", refused.Code);
        Assert.NotEqual(stranger, ReleaseKeys.BuiltIn[0]);
        Directory.Delete(dir, recursive: true);
    }
}
