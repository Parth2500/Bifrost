using Bifrost.App;
using Bifrost.App.Desk;
using Bifrost.App.Service;

return args switch
{
    ["daemon"] => await DaemonCommand.RunAsync(),
    ["service", .. var rest] => await new ServiceCommand(new ProcessCommandRunner(), Environment.ProcessPath ?? "",
        Environment.GetEnvironmentVariable, Environment.IsPrivilegedProcess, Console.Out, new RealFileSystem()).RunAsync(rest),
    ["desk", .. var rest] => await new DeskCommand(new ProcessCommandRunner(), Environment.ProcessPath ?? "",
        Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.IsPrivilegedProcess, Console.Out, new RealFileSystem()).RunAsync(rest),
    ["identity", .. var rest] => await IdentityCommand.RunAsync(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Usage: bifrost <command>

        Commands:
          daemon                              Run the home: people, devices, extensions and the API.
          service install [--admin <account>] Install and start the system service (needs sudo).
          service uninstall [--purge]         Remove the service; --purge also removes its data.
          service status                      Show the service's state.
          desk                                Show the admin's questions as desktop notifications.
          identity rotate                     Make a new home key (asks for the system password). Devices pair again.
          desk install | uninstall            Run that in your desktop session (no sudo).
        """);
    return 64; // EX_USAGE
}
