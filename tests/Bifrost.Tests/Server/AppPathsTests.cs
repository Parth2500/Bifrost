using Bifrost.Server.Hosting;

namespace Bifrost.Tests.Server;

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Uses_systemd_directories_when_running_as_a_service()
    {
        var paths = AppPaths.Resolve(Env(
            ("CONFIGURATION_DIRECTORY", "/etc/bifrost"), ("STATE_DIRECTORY", "/var/lib/bifrost"),
            ("RUNTIME_DIRECTORY", "/run/bifrost"), ("XDG_RUNTIME_DIR", "/run/user/1000")), "/home/u");

        Assert.Equal(new AppPaths("/etc/bifrost", "/var/lib/bifrost", "/run/bifrost"), paths);
    }

    [Fact]
    public void Takes_the_first_entry_of_a_colon_separated_systemd_directory()
    {
        var paths = AppPaths.Resolve(Env(("STATE_DIRECTORY", "/var/lib/bifrost:/var/lib/other")), "/home/u");

        Assert.Equal("/var/lib/bifrost", paths.Data);
    }

    [Fact]
    public void Falls_back_to_XDG_folders_named_bifrost_for_a_developer()
    {
        var paths = AppPaths.Resolve(Env(), "/home/u");

        Assert.Equal(new AppPaths("/home/u/.config/bifrost", "/home/u/.local/share/bifrost", "/home/u/.local/state/bifrost"), paths);
    }

    [Fact]
    public void Honours_XDG_variables()
    {
        var paths = AppPaths.Resolve(Env(("XDG_CONFIG_HOME", "/c"), ("XDG_DATA_HOME", "/d"), ("XDG_RUNTIME_DIR", "/run/user/1000")), "/home/u");

        Assert.Equal(new AppPaths("/c/bifrost", "/d/bifrost", "/run/user/1000/bifrost"), paths);
    }

    [Fact]
    public void Empty_variables_count_as_unset()
    {
        var paths = AppPaths.Resolve(Env(("STATE_DIRECTORY", ""), ("XDG_DATA_HOME", "")), "/home/u");

        Assert.Equal("/home/u/.local/share/bifrost", paths.Data);
    }
}
