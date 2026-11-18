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
    ["invite", .. var rest] => await InviteCommand.RunAsync(rest),
    ["identity", .. var rest] => await IdentityCommand.RunAsync(rest),
    ["release", .. var rest] => await ReleaseCommand.RunAsync(rest),
    ["extension", .. var rest] => await ExtensionCommand.RunAsync(rest, new ProcessCommandRunner()),
    ["open", .. var rest] => await OpenCommand.RunAsync(rest, new ProcessCommandRunner()),
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
          open [--print]                      Open the host screen in the browser on this machine.
          release keygen|pack|sign|verify     For the owner: make a key, build and sign an extension release.
          extension install --from <file>     Install a signed extension package (asks for the system password).
          extension rollback <name>           Go back to the version before.
          invite [person]                     Print an invitation link and QR code for a new device.
          identity rotate                     Make a new home key (asks for the system password). Devices pair again.
          desk install | uninstall            Run that in your desktop session (no sudo).
        """);
    return 64; // EX_USAGE
}
