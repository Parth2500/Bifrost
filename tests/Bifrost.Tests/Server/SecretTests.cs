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

    [Fact]
    public void LoadOrCreate_returns_the_same_43_character_token_each_time()
    {
        var first = LocalAdminToken.LoadOrCreate(_dir);
        var second = LocalAdminToken.LoadOrCreate(_dir);

        Assert.Equal(43, first.Length);
        Assert.Equal(first, second);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
    }

    [Fact]
    public void Tokens_from_different_folders_differ()
    {
        Assert.NotEqual(LocalAdminToken.LoadOrCreate(Path.Combine(_dir, "a")), LocalAdminToken.LoadOrCreate(Path.Combine(_dir, "b")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abd1234567890123456789012345678901234567890")]
    public void Matches_rejects_missing_short_and_wrong_tokens(string? presented)
    {
        Assert.False(LocalAdminToken.Matches("abc1234567890123456789012345678901234567890", presented));
    }

    [Fact]
    public void Matches_accepts_the_exact_token()
    {
        Assert.True(LocalAdminToken.Matches("abc", "abc"));
    }

    [Fact]
    public void An_empty_token_file_left_by_a_crash_is_replaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "admin.token"), "");

        var token = LocalAdminToken.LoadOrCreate(_dir);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(token, File.ReadAllText(Path.Combine(_dir, "admin.token")).Trim());
    }

    [Fact]
    public void A_token_file_of_the_wrong_shape_is_not_trusted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "admin.token"), "abc");

        var token = LocalAdminToken.LoadOrCreate(_dir);

        Assert.NotEqual("abc", token);
        Assert.Equal(43, token.Length);
    }

    [Fact]
    public void A_token_with_loose_permissions_is_replaced_because_someone_may_have_read_it()
    {
        var path = Path.Combine(_dir, "admin.token");
        var original = LocalAdminToken.LoadOrCreate(_dir);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var loaded = LocalAdminToken.LoadOrCreate(_dir);

        Assert.NotEqual(original, loaded);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_symlinked_token_file_is_replaced_and_its_target_left_alone()
    {
        Directory.CreateDirectory(_dir);
        var planted = Path.Combine(Path.GetTempPath(), "bifrost-planted-" + Guid.NewGuid().ToString("N"));
        const string known = "abc1234567890123456789012345678901234567890";
        File.WriteAllText(planted, known);
        File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var path = Path.Combine(_dir, "admin.token");
        File.CreateSymbolicLink(path, planted);
        try
        {
            var token = LocalAdminToken.LoadOrCreate(_dir);

            Assert.NotEqual(known, token);
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal(known, File.ReadAllText(planted));
        }
        finally
        {
            File.Delete(planted);
        }
    }

    [Fact]
    public void An_open_runtime_folder_is_made_owner_only()
    {
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, (UnixFileMode)0b111_101_101); // 0755, systemd's default for RuntimeDirectory

        LocalAdminToken.LoadOrCreate(_dir);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }
}
