using System.Net.Sockets;
using System.Runtime.InteropServices;
using Bifrost.App.Service;
using Bifrost.Server.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.App.Desk;

/// <summary>
/// <c>bifrost desk</c>: the desk companion (design §4, §5.1). It runs in the admin's desktop session,
/// because a system service can show nothing on a screen, and talks to Bifröst on the admin socket.
/// <c>bifrost desk install | uninstall</c> sets it up as a user service.
/// </summary>
public sealed class DeskCommand(
    ICommandRunner runner, string processPath, Func<string, string?> env, string home, bool isRoot, TextWriter output, IFileSystem fs)
{
    /// <summary>The service's admin socket; a daemon run by hand has its own under the user's runtime folder.</summary>
    public const string ServiceSocket = "/run/bifrost-admin/admin.sock";

    private const UnixFileMode Mode755 = (UnixFileMode)0b111_101_101;
    private const UnixFileMode Mode644 = (UnixFileMode)0b110_100_100;

    private string UnitFolder => Path.Combine(env("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"), "systemd", "user");
    private string UnitPath => Path.Combine(UnitFolder, DeskUnit.Name);

    public Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args switch
        {
            [] => WatchAsync(),
            ["install"] => InstallAsync(),
            ["uninstall"] => UninstallAsync(),
            _ => Usage(),
        };
    }

    private async Task<int> InstallAsync()
    {
        if (isRoot) return Refuse("Run it as yourself, without sudo: the desk companion belongs to your desktop session.");
        // The installed program when there is one, so the unit survives the build folder going away.
        var binary = fs.FileExists(ServiceCommand.Binary) ? ServiceCommand.Binary : processPath;
        if (Path.GetFileNameWithoutExtension(binary) is "dotnet" or "")
        {
            return Refuse("Install Bifröst first (sudo bifrost service install), or run this from the published bifrost program.");
        }
        if (binary.Any(c => char.IsWhiteSpace(c) || c is '"' or '%' or '$' or '\\'))
        {
            return Refuse("The path to bifrost has a space or a special character; the desk unit cannot name it.");
        }

        Say($"write {UnitPath}");
        fs.CreateDirectory(UnitFolder, Mode755);
        fs.WriteFile(UnitPath, DeskUnit.Render(binary), Mode644);
        if (!await Run("systemctl", "--user", "daemon-reload")) return 1;
        if (!await Run("systemctl", "--user", "enable", "--now", DeskUnit.Name)) return 1;
        output.WriteLine("Installed. Questions for the admin now show as notifications while you are logged in.");
        return 0;
    }

    private async Task<int> UninstallAsync()
    {
        if (isRoot) return Refuse("Run it as yourself, without sudo.");
        await Run("systemctl", "--user", "disable", "--now", DeskUnit.Name); // not installed is fine
        Say($"remove {UnitPath}");
        fs.Delete(UnitPath);
        await Run("systemctl", "--user", "daemon-reload");
        output.WriteLine("Removed the desk companion.");
        return 0;
    }

    private async Task<int> WatchAsync()
    {
        using var stop = new CancellationTokenSource();
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        using var logs = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));
        using var http = Client(() => AdminSocket(env, AppPaths.ForCurrentProcess().AdminSocket));
        await new DeskLoop(http, new NotifySendNotifier(), TimeProvider.System, logs.CreateLogger("Bifrost.Desk")).RunAsync(stop.Token);
        return 0;
    }

    /// <summary>Where to find Bifröst: <c>BIFROST_ADMIN_SOCKET</c>, else the service's socket when it exists, else this user's own daemon.</summary>
    public static string AdminSocket(Func<string, string?> env, string ownSocket)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env("BIFROST_ADMIN_SOCKET") is { Length: > 0 } chosen) return chosen;
        return File.Exists(ServiceSocket) || !File.Exists(ownSocket) ? ServiceSocket : ownSocket;
    }

    /// <summary>HTTP over the admin socket. The path is looked up at each connection, so Bifröst may appear later.</summary>
    public static HttpClient Client(Func<string> socketPath) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath()), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(90) };

    private Task<int> Usage()
    {
        output.WriteLine("Usage: bifrost desk [install | uninstall]");
        return Task.FromResult(64);
    }

    private int Refuse(string why)
    {
        output.WriteLine(why);
        return 1;
    }

    private void Say(string action) => output.WriteLine("+ " + action);

    private async Task<bool> Run(string file, params string[] args)
    {
        Say($"{file} {string.Join(' ', args)}");
        var (exit, text) = await runner.RunAsync(file, args);
        if (exit == 0) return true;
        output.WriteLine($"{file} failed (exit {exit.ToString(System.Globalization.CultureInfo.InvariantCulture)}):");
        if (text.Length > 0) output.WriteLine(text);
        return false;
    }
}
