using Bifrost.App.Service;

namespace Bifrost.Tests;

public sealed class ServiceCommandTests : IDisposable
{
    private const string Published = "/home/parth/publish/bifrost";
    private readonly FakeRunner _runner = new();
    private readonly FakeFiles _files = new();
    private readonly StringWriter _output = new();
    private readonly Dictionary<string, string> _env = new() { ["SUDO_USER"] = "parth", ["SUDO_UID"] = "1000" };

    public void Dispose() => _output.Dispose();

    private ServiceCommand Command(string processPath = Published, bool isRoot = true) =>
        new(_runner, processPath, _env.GetValueOrDefault, isRoot, _output, _files);

    private sealed class FakeRunner : ICommandRunner
    {
        public List<string> Calls { get; } = [];
        public Func<string, (int, string)> Respond { get; set; } = _ => (0, "");

        public Task<(int Exit, string Output)> RunAsync(string file, params string[] args)
        {
            var line = file + " " + string.Join(' ', args);
            Calls.Add(line);
            return Task.FromResult(Respond(line));
        }
    }

    private sealed class FakeFiles : IFileSystem
    {
        public Dictionary<string, (string Content, UnixFileMode Mode)> Files { get; } = new() { [Published] = ("<program>", (UnixFileMode)0b111_101_101) };
        public Dictionary<string, UnixFileMode> Folders { get; } = [];
        public List<string> Deleted { get; } = [];

        public bool FileExists(string path) => Files.ContainsKey(path);

        public IReadOnlyList<string> FilesEndingWith(string folder, string suffix) =>
            [.. Files.Keys.Where(f => Path.GetDirectoryName(f) == folder && f.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        public void CreateDirectory(string path, UnixFileMode mode) => Folders[path] = mode;

        public void WriteFile(string path, string content, UnixFileMode mode) => Files[path] = (content, mode);

        public void CopyFile(string source, string target, UnixFileMode mode) => Files[target] = (Files[source].Content, mode);

        public void Delete(string path)
        {
            Deleted.Add(path);
            Files.Remove(path);
            Folders.Remove(path);
            foreach (var inside in Files.Keys.Where(f => f.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) Files.Remove(inside);
        }
    }

    [Fact]
    public void The_unit_is_sandboxed()
    {
        var unit = SystemUnit.Render("/opt/bifrost/bifrost");

        foreach (var line in new[]
        {
            "Type=simple", "User=bifrost", "Group=bifrost", "ExecStart=/opt/bifrost/bifrost daemon", "Restart=on-failure", "RestartSec=5",
            "StateDirectory=bifrost", "StateDirectoryMode=0700", "ConfigurationDirectory=bifrost", "RuntimeDirectory=bifrost bifrost-admin",
            "RuntimeDirectoryMode=0755", "UMask=0077", "NoNewPrivileges=yes", "ProtectSystem=strict", "ProtectHome=yes", "PrivateTmp=yes",
            "PrivateDevices=yes", "ProtectKernelTunables=yes", "ProtectKernelModules=yes", "ProtectControlGroups=yes",
            "RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6", "RestrictNamespaces=yes", "LockPersonality=yes",
            "SystemCallArchitectures=native", "TimeoutStopSec=20", "After=network.target", "WantedBy=multi-user.target",
        })
        {
            Assert.Contains("\n" + line + "\n", unit, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("\r", unit, StringComparison.Ordinal);
        Assert.StartsWith("[Unit]\n", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_refuses_without_root()
    {
        var code = await Command(isRoot: false).RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("sudo bifrost service install", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_runner.Calls);
        Assert.Single(_files.Files); // only the published program
    }

    [Fact]
    public async Task Install_refuses_from_the_dotnet_host()
    {
        var code = await Command(processPath: "/usr/lib64/dotnet/dotnet").RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("not through dotnet", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Install_creates_the_account_only_when_missing()
    {
        _runner.Respond = line => line == "id -u bifrost" ? (1, "id: 'bifrost': no such user") : (0, "");
        await Command().RunAsync(["install"]);
        Assert.Contains("useradd --system --user-group --no-create-home --shell /usr/sbin/nologin bifrost", _runner.Calls);

        _runner.Calls.Clear();
        _runner.Respond = _ => (0, "993");
        await Command().RunAsync(["install"]);
        Assert.DoesNotContain(_runner.Calls, call => call.StartsWith("useradd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_copies_the_program_writes_the_unit_and_starts_it()
    {
        _files.Files["/home/parth/publish/libe_sqlite3.so"] = ("<sqlite>", (UnixFileMode)0b110_100_100);

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(0, code);
        Assert.Equal(("<program>", (UnixFileMode)0b111_101_101), _files.Files["/opt/bifrost/bifrost"]);
        Assert.Equal("<sqlite>", _files.Files["/opt/bifrost/libe_sqlite3.so"].Content);
        Assert.Equal((SystemUnit.Render("/opt/bifrost/bifrost"), (UnixFileMode)0b110_100_100), _files.Files["/etc/systemd/system/bifrost.service"]);
        Assert.Equal(
            ["id -u bifrost", "chown bifrost:bifrost /etc/bifrost/admin.json", "systemctl daemon-reload", "systemctl enable --now bifrost.service"],
            _runner.Calls);
    }

    [Fact]
    public async Task Install_says_each_thing_before_doing_it()
    {
        await Command().RunAsync(["install"]);

        var said = _output.ToString();
        foreach (var call in _runner.Calls) Assert.Contains("+ " + call, said, StringComparison.Ordinal);
        Assert.Contains("+ copy /home/parth/publish/bifrost to /opt/bifrost/bifrost", said, StringComparison.Ordinal);
        Assert.Contains("+ write /etc/systemd/system/bifrost.service", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_records_the_sudo_user_as_admin()
    {
        await Command().RunAsync(["install"]);

        var (content, mode) = _files.Files["/etc/bifrost/admin.json"];
        Assert.Equal("{\"uid\":1000,\"name\":\"parth\"}\n", content);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task Install_takes_admin_from_the_option()
    {
        _runner.Respond = line => line == "id -u -- mira" ? (0, "1001\n") : (0, "");

        var code = await Command().RunAsync(["install", "--admin", "mira"]);

        Assert.Equal(0, code);
        Assert.Equal("{\"uid\":1001,\"name\":\"mira\"}\n", _files.Files["/etc/bifrost/admin.json"].Content);
    }

    [Fact]
    public async Task An_unknown_admin_account_is_refused()
    {
        _runner.Respond = line => line == "id -u -- nobody-here" ? (1, "no such user") : (0, "");

        var code = await Command().RunAsync(["install", "--admin", "nobody-here"]);

        Assert.Equal(1, code);
        Assert.False(_files.FileExists("/etc/bifrost/admin.json"));
        Assert.False(_files.FileExists("/etc/systemd/system/bifrost.service"));
    }

    [Fact]
    public async Task Root_cannot_be_the_admin()
    {
        _env["SUDO_USER"] = "root";
        _env["SUDO_UID"] = "0";

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("Root cannot be the admin", _output.ToString(), StringComparison.Ordinal);
        Assert.False(_files.FileExists("/etc/bifrost/admin.json"));
        Assert.False(_files.FileExists("/opt/bifrost/bifrost"));
    }

    [Fact]
    public async Task Without_sudo_variables_or_the_option_the_admin_must_be_named()
    {
        _env.Clear();

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("--admin", _output.ToString(), StringComparison.Ordinal);
        Assert.False(_files.FileExists("/etc/systemd/system/bifrost.service"));
    }

    [Fact]
    public async Task Install_twice_gives_the_same_files_and_commands()
    {
        await Command().RunAsync(["install"]);
        var files = _files.Files.ToDictionary();
        var calls = _runner.Calls.ToList();
        _runner.Calls.Clear();

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(0, code);
        Assert.Equal(files, _files.Files);
        Assert.Equal(calls, _runner.Calls);
        Assert.Empty(_files.Deleted); // the data is never touched
    }

    [Fact]
    public async Task Installing_from_the_installed_program_does_not_copy_it_onto_itself()
    {
        _files.Files["/opt/bifrost/bifrost"] = ("<installed>", (UnixFileMode)0b111_101_101);

        var code = await Command(processPath: "/opt/bifrost/bifrost").RunAsync(["install"]);

        Assert.Equal(0, code);
        Assert.Equal("<installed>", _files.Files["/opt/bifrost/bifrost"].Content);
    }

    [Fact]
    public async Task A_failed_systemctl_is_reported_and_fails_the_install()
    {
        _runner.Respond = line => line.StartsWith("systemctl enable", StringComparison.Ordinal) ? (1, "Unit is masked.") : (0, "");

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("Unit is masked.", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Installed.", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_keeps_data_and_says_so()
    {
        await Command().RunAsync(["install"]);
        _runner.Calls.Clear();

        var code = await Command().RunAsync(["uninstall"]);

        Assert.Equal(0, code);
        Assert.Equal(["systemctl disable --now bifrost.service", "systemctl daemon-reload"], _runner.Calls);
        Assert.Equal(["/etc/systemd/system/bifrost.service"], _files.Deleted);
        Assert.True(_files.FileExists("/etc/bifrost/admin.json"));
        Assert.Contains("Kept: /var/lib/bifrost", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--purge", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_purge_removes_everything()
    {
        await Command().RunAsync(["install"]);
        _runner.Calls.Clear();

        var code = await Command().RunAsync(["uninstall", "--purge"]);

        Assert.Equal(0, code);
        Assert.Equal(["/etc/systemd/system/bifrost.service", "/opt/bifrost", "/var/lib/bifrost", "/etc/bifrost"], _files.Deleted);
        Assert.Equal(["systemctl disable --now bifrost.service", "systemctl daemon-reload", "userdel bifrost"], _runner.Calls);
        Assert.False(_files.FileExists("/opt/bifrost/bifrost"));
        Assert.False(_files.FileExists("/etc/bifrost/admin.json"));
    }

    [Fact]
    public async Task Uninstall_refuses_without_root()
    {
        var code = await Command(isRoot: false).RunAsync(["uninstall", "--purge"]);

        Assert.Equal(1, code);
        Assert.Empty(_runner.Calls);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Status_shows_what_systemctl_says_and_needs_no_root()
    {
        _runner.Respond = _ => (3, "bifrost.service - Bifröst, the home\n   Active: inactive (dead)");

        var code = await Command(isRoot: false).RunAsync(["status"]);

        Assert.Equal(3, code);
        Assert.Equal(["systemctl status bifrost.service --no-pager"], _runner.Calls);
        Assert.Contains("inactive (dead)", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("install", "--admin")]
    [InlineData("uninstall", "--everything")]
    public async Task Anything_else_is_a_usage_error(params string[] args)
    {
        var code = await Command().RunAsync(args);

        Assert.Equal(64, code);
        Assert.Empty(_runner.Calls);
    }
}
