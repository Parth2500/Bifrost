namespace Bifrost.Server.Hosting;

/// <summary>
/// Where Bifröst keeps its settings, data and runtime files. As a system service (design §3) systemd
/// hands these over in <c>CONFIGURATION_DIRECTORY</c>, <c>STATE_DIRECTORY</c> and <c>RUNTIME_DIRECTORY</c>;
/// a developer running it by hand gets per-user XDG folders instead.
/// </summary>
/// <param name="Runtime">Bifröst's own runtime folder, <c>0700</c>.</param>
/// <param name="AdminRuntime">A second runtime folder other accounts may enter; it holds only the admin socket.</param>
public sealed record AppPaths(string Settings, string Data, string Runtime, string AdminRuntime)
{
    private const string AppFolder = "bifrost";
    private const string AdminFolder = "bifrost-admin";

    /// <summary>Where the host admin reaches Bifröst without a token.</summary>
    public string AdminSocket => Path.Combine(AdminRuntime, "admin.sock");

    public static AppPaths ForCurrentProcess() =>
        Resolve(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static AppPaths Resolve(Func<string, string?> env, string home)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? Get(string name) => env(name) is { Length: > 0 } value ? value : null;

        // systemd joins several directories with ':'; the first is the one named after the unit.
        string? Systemd(string name) => Get(name)?.Split(':')[0];

        var config = Get("XDG_CONFIG_HOME") ?? $"{home}/.config";
        var data = Get("XDG_DATA_HOME") ?? $"{home}/.local/share";
        var runtime = Get("XDG_RUNTIME_DIR") ?? Get("XDG_STATE_HOME") ?? $"{home}/.local/state";
        var ownRuntime = Systemd("RUNTIME_DIRECTORY") ?? $"{runtime}/{AppFolder}";
        // The unit names two runtime folders, "bifrost bifrost-admin"; systemd lists them in that order.
        var adminRuntime = Get("RUNTIME_DIRECTORY")?.Split(':') is { Length: > 1 } entries && entries[1].Length > 0
            ? entries[1]
            : Path.Combine(Path.GetDirectoryName(ownRuntime.TrimEnd('/')) ?? "/", AdminFolder);
        return new AppPaths(
            Systemd("CONFIGURATION_DIRECTORY") ?? $"{config}/{AppFolder}",
            Systemd("STATE_DIRECTORY") ?? $"{data}/{AppFolder}",
            ownRuntime,
            adminRuntime);
    }
}
