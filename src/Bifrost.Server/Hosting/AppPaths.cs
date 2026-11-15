namespace Bifrost.Server.Hosting;

/// <summary>
/// Where Bifröst keeps its settings, data and runtime files. As a system service (design §3) systemd
/// hands these over in <c>CONFIGURATION_DIRECTORY</c>, <c>STATE_DIRECTORY</c> and <c>RUNTIME_DIRECTORY</c>;
/// a developer running it by hand gets per-user XDG folders instead.
/// </summary>
public sealed record AppPaths(string Settings, string Data, string Runtime)
{
    private const string AppFolder = "bifrost";

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
        return new AppPaths(
            Systemd("CONFIGURATION_DIRECTORY") ?? $"{config}/{AppFolder}",
            Systemd("STATE_DIRECTORY") ?? $"{data}/{AppFolder}",
            Systemd("RUNTIME_DIRECTORY") ?? $"{runtime}/{AppFolder}");
    }
}
