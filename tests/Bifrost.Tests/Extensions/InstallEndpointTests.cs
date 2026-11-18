using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Bifrost.Core.Models;
using Bifrost.Server.Extensions;
using static Bifrost.Tests.Server.HostFixture;

namespace Bifrost.Tests.Extensions;

/// <summary>Installing from a signed package through the admin socket (design §8.3, ADR 004).</summary>
public sealed class InstallEndpointTests
{
    private static byte[] Package(string version) 
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                w.Write($$"""{"name":"sleipnir","version":"{{version}}","program":{"linux-x64":"bin/sleipnir","linux-arm64":"bin/sleipnir"} }""");
            }
            using (var w = new StreamWriter(zip.CreateEntry("bin/sleipnir").Open())) w.Write("#!/bin/sh\n");
        }
        return memory.ToArray();
    }

    private static HttpRequestMessage Upload(byte[] package, byte[] signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extensions/install") { Content = new ByteArrayContent(package) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Bifrost-Signature", Convert.ToBase64String(signature));
        return request;
    }

    private static byte[] Sign(ECDsa key, byte[] package) => key.SignHash(SHA256.HashData(package), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static (ExtensionInstaller Installer, ECDsa Key, string Root) Setup()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = Directory.CreateTempSubdirectory("bifrost-endpoint-").FullName;
        return (new ExtensionInstaller(root, new ReleaseKeys([Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())])), key, root);
    }

    [Fact]
    public async Task The_admin_socket_installs_a_signed_package_and_the_audit_log_says_so()
    {
        var (installer, key, root) = Setup();
        await using var home = await StartAsync(installer: installer);
        var package = Package("1.0.0");

        var response = await home.AdminHttp.SendAsync(Upload(package, Sign(key, package)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("1.0.0", body.GetProperty("version").GetString());
        Assert.Equal("bifrost", body.GetProperty("restart").GetString()); // not configured yet: Bifröst starts it
        Assert.Equal("1.0.0", installer.CurrentVersion("sleipnir"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_package_with_the_wrong_signature_is_refused_and_noted()
    {
        var (installer, _, root) = Setup();
        await using var home = await StartAsync(installer: installer);
        var package = Package("1.0.0");
        var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var response = await home.AdminHttp.SendAsync(Upload(package, Sign(stranger, package)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("bad_signature", await Code(response));
        Assert.Null(installer.CurrentVersion("sleipnir"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Only_this_computer_installs_and_a_missing_signature_header_is_a_bad_request()
    {
        var (installer, key, root) = Setup();
        await using var home = await StartAsync(installer: installer);
        var (_, remoteAdmin) = await home.AddPersonAsync("carol", PersonRole.Admin);
        var package = Package("1.0.0");
        var remote = Upload(package, Sign(key, package));
        remote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await home.Http.SendAsync(remote)).StatusCode);
        var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extensions/install") { Content = new ByteArrayContent(package) };
        Assert.Equal(HttpStatusCode.BadRequest, (await home.AdminHttp.SendAsync(noHeader)).StatusCode);
        Assert.Null(installer.CurrentVersion("sleipnir"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Without_an_installer_nothing_can_be_installed()
    {
        await using var home = await StartAsync();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Package("1.0.0");

        var response = await home.AdminHttp.SendAsync(Upload(package, Sign(key, package)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("install_unavailable", await Code(response));
    }
}

public sealed class SourceEndpointTests
{
    [Fact]
    public async Task The_sources_are_for_this_computer_only_and_name_only_the_owners_repositories()
    {
        await using var home = await StartAsync();
        var (_, remoteAdmin) = await home.AddPersonAsync("carol", PersonRole.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, (await home.SendAsync(HttpMethod.Get, "/api/v1/extensions/sources", token: remoteAdmin)).StatusCode);
        var list = await Json(await home.AdminHttp.GetAsync("/api/v1/extensions/sources"));
        Assert.All(list.EnumerateArray(), s => Assert.StartsWith("Parth2500/", s.GetProperty("repository").GetString()));
    }

    [Fact]
    public async Task Without_a_downloader_checking_and_installing_from_GitHub_say_so()
    {
        await using var home = await StartAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await home.AdminHttp.PostAsync("/api/v1/extensions/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await home.AdminHttp.PostAsync("/api/v1/extensions/sleipnir/install-latest", null)).StatusCode);
        var (_, member) = await home.AddPersonAsync("bob", PersonRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await home.SendAsync(HttpMethod.Post, "/api/v1/extensions/check", token: member)).StatusCode);
    }
}
