using Bifrost.Server.Security;

namespace Bifrost.Tests.Server;

public sealed class SecretTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bifrost-secret-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateNew_makes_the_file_private_and_its_folder_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "run", "secret");

        SecretFile.CreateNew(path, "hunter2");

        Assert.Equal("hunter2", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void CreateNew_refuses_to_overwrite_and_keeps_the_old_content()
    {
        var path = Path.Combine(_dir, "secret");
        SecretFile.CreateNew(path, "first");

        Assert.Throws<IOException>(() => SecretFile.CreateNew(path, "second"));

        Assert.Equal("first", File.ReadAllText(path));
    }
}
