using System.Globalization;
using System.Text.Json;

namespace Bifrost.App.Service;

/// <summary>
/// <c>bifrost service install | uninstall | status</c>: Bifröst as a systemd system service under its
/// own account (design §3). Install and uninstall need root, and print each thing they do before doing it.
/// </summary>
public sealed class ServiceCommand(
    ICommandRunner runner, string processPath, Func<string, string?> env, bool isRoot, TextWriter output, IFileSystem fs)
{
    public const string InstallFolder = "/opt/bifrost";
    public const string Binary = InstallFolder + "/bifrost";
    public const string SettingsFolder = "/etc/bifrost";
    public const string DataFolder = "/var/lib/bifrost";
    public const string UnitPath = "/etc/systemd/system/" + SystemUnit.Name;
    public const string AdminFile = SettingsFolder + "/admin.json";

    private const UnixFileMode Mode755 = (UnixFileMode)0b111_101_101;
    private const UnixFileMode Mode644 = (UnixFileMode)0b110_100_100;
    private const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args switch
        {
            ["install"] => InstallAsync(null),
            ["install", "--admin", var user] => InstallAsync(user),
            ["uninstall"] => UninstallAsync(purge: false),
            ["uninstall", "--purge"] => UninstallAsync(purge: true),
            ["status"] => StatusAsync(),
            _ => Usage(),
        };
    }

    private async Task<int> InstallAsync(string? adminOption)
    {
        if (!isRoot) return Refuse("Run it with sudo: sudo bifrost service install");
        if (Path.GetFileNameWithoutExtension(processPath) is "dotnet" or "")
        {
            return Refuse("Install from the published bifrost program, not through dotnet: the service needs one program file to copy.");
        }
        if (await AdminAsync(adminOption) is not var (uid, name)) return 1;

        var (missing, _) = await Quiet("id", "-u", SystemUnit.Account);
        if (missing != 0 && !await Run("useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", SystemUnit.Account))
        {
            return 1;
        }

        if (!string.Equals(processPath, Binary, StringComparison.Ordinal))
        {
            Say($"copy {processPath} to {Binary}");
            fs.CreateDirectory(InstallFolder, Mode755);
            fs.CopyFile(processPath, Binary, Mode755);
            // Native libraries published beside the program (SQLite) are not inside the single file.
            foreach (var library in fs.FilesEndingWith(Path.GetDirectoryName(processPath) ?? "/", ".so"))
            {
                fs.CopyFile(library, Path.Combine(InstallFolder, Path.GetFileName(library)), Mode755);
            }
        }

        Say($"write {AdminFile} (admin: {name}, uid {uid.ToString(CultureInfo.InvariantCulture)})");
        fs.CreateDirectory(SettingsFolder, Mode755);
        fs.WriteFile(AdminFile, JsonSerializer.Serialize(new { uid, name }) + "\n", Mode600);
        if (!await Run("chown", $"{SystemUnit.Account}:{SystemUnit.Account}", AdminFile)) return 1;

        Say($"write {UnitPath}");
        fs.WriteFile(UnitPath, SystemUnit.Render(Binary), Mode644);
        if (!await Run("systemctl", "daemon-reload")) return 1;
        if (!await Run("systemctl", "enable", "--now", SystemUnit.Name)) return 1;

        output.WriteLine($"Installed. Bifröst runs as the '{SystemUnit.Account}' account, and {name} is its admin.");
        output.WriteLine("Try it: curl --unix-socket /run/bifrost-admin/admin.sock http://localhost/api/v1/me");
        output.WriteLine($"After installing a newer version: sudo systemctl restart {SystemUnit.Name}");
        return 0;
    }

    /// <summary>The account to record as admin: <c>--admin</c>, else whoever ran sudo. Never root.</summary>
    private async Task<(uint Uid, string Name)?> AdminAsync(string? option)
    {
        string? name;
        string? uidText;
        if (option is not null)
        {
            name = option;
            var (exit, text) = await Quiet("id", "-u", "--", option);
            if (exit != 0)
            {
                Refuse($"There is no account named '{option}'.");
                return null;
            }
            uidText = text.Trim();
        }
        else
        {
            name = env("SUDO_USER");
            uidText = env("SUDO_UID");
        }

        if (string.IsNullOrEmpty(name) || !uint.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
        {
            Refuse("Could not tell who the admin is. Say it: sudo bifrost service install --admin <your account>");
            return null;
        }
        if (uid == 0)
        {
            Refuse("Root cannot be the admin. Say whose computer this is: sudo bifrost service install --admin <your account>");
            return null;
        }
        return (uid, name);
    }

    private async Task<int> UninstallAsync(bool purge)
    {
        if (!isRoot) return Refuse("Run it with sudo: sudo bifrost service uninstall");

        await Run("systemctl", "disable", "--now", SystemUnit.Name); // not installed is fine
        Say($"remove {UnitPath}");
        fs.Delete(UnitPath);
        await Run("systemctl", "daemon-reload");

        if (purge)
        {
            foreach (var folder in new[] { InstallFolder, DataFolder, SettingsFolder })
            {
                Say($"remove {folder}");
                fs.Delete(folder);
            }
            await Run("userdel", SystemUnit.Account);
            output.WriteLine("Removed Bifröst, its data, its settings and its account.");
        }
        else
        {
            output.WriteLine($"Removed the service. Kept: {DataFolder} (people, devices and the extensions' data), {SettingsFolder}, {InstallFolder} and the '{SystemUnit.Account}' account.");
            output.WriteLine($"To remove those too: sudo {Binary} service uninstall --purge");
        }
        return 0;
    }

    private async Task<int> StatusAsync()
    {
        var (exit, text) = await runner.RunAsync("systemctl", "status", SystemUnit.Name, "--no-pager");
        output.WriteLine(text);
        return exit;
    }

    private Task<int> Usage()
    {
        output.WriteLine("Usage: bifrost service install [--admin <account>] | uninstall [--purge] | status");
        return Task.FromResult(64);
    }

    private int Refuse(string why)
    {
        output.WriteLine(why);
        return 1;
    }

    private void Say(string action) => output.WriteLine("+ " + action);

    /// <summary>Prints the command, runs it, and reports a failure.</summary>
    private async Task<bool> Run(string file, params string[] args)
    {
        Say($"{file} {string.Join(' ', args)}");
        var (exit, text) = await runner.RunAsync(file, args);
        if (exit == 0) return true;
        output.WriteLine($"{file} {args[0]} failed (exit {exit.ToString(CultureInfo.InvariantCulture)}):");
        if (text.Length > 0) output.WriteLine(text);
        return false;
    }

    /// <summary>A question, not a change: printed too, but a non-zero answer is not a failure.</summary>
    private Task<(int Exit, string Output)> Quiet(string file, params string[] args)
    {
        Say($"{file} {string.Join(' ', args)}");
        return runner.RunAsync(file, args);
    }
}
