using Bifrost.App;

return args switch
{
    ["daemon"] => await DaemonCommand.RunAsync(),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Usage: bifrost <command>

        Commands:
          daemon    Run the home: people, devices and the loopback API.
        """);
    return 64; // EX_USAGE
}
