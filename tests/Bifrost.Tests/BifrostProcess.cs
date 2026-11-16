using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Bifrost.Server.Hosting;

namespace Bifrost.Tests;

/// <summary>
/// A throwaway machine for the real <c>bifrost</c>: the three systemd directory variables point into a
/// temp folder, as systemd would set them for the service, so no test touches the real machine.
/// </summary>
public sealed class BifrostMachine : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bifrost-machine-" + Guid.NewGuid().ToString("N"));
    public string RuntimeDir => Path.Combine(Root, "run");
    public string DataDir => Path.Combine(Root, "state");
    public string ConfigDir => Path.Combine(Root, "config");
    public string TokenPath => Path.Combine(RuntimeDir, "admin.token");
    public string AdminSocket => Path.Combine(Root, "bifrost-admin", "admin.sock");

    /// <summary>The host admin's way in: the admin socket, as the account running the tests.</summary>
    public HttpClient AdminClient() => UnixHttp.Client(AdminSocket);
    public string DatabasePath => Path.Combine(DataDir, "bifrost.db");

    public BifrostMachine() => Directory.CreateDirectory(Root);

    public BifrostProcess Start(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "bifrost.dll");
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["HOME"] = Path.Combine(Root, "home");
        info.Environment["STATE_DIRECTORY"] = DataDir;
        info.Environment["RUNTIME_DIRECTORY"] = RuntimeDir;
        info.Environment["CONFIGURATION_DIRECTORY"] = ConfigDir;
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        return new BifrostProcess(Process.Start(info)!);
    }

    public async Task<DaemonInfo> WaitForDaemonAsync(BifrostProcess process, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (DaemonInfo.TryRead(RuntimeDir) is { } info && info.Pid == process.Id) return info;
            if (process.HasExited) throw new InvalidOperationException("bifrost exited early:\n" + process.Output);
            await Task.Delay(50);
        }
        throw new TimeoutException("daemon.json never appeared:\n" + process.Output);
    }

    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class BifrostProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public BifrostProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public string Output { get { lock (_output) return _output.ToString(); } }

    public async Task<int> WaitForExitAsync(int seconds = 15)
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
        return _process.ExitCode;
    }

    public void Signal(int signal) => _ = NativeMethods.Kill(_process.Id, signal);

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}

internal static class NativeMethods
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static extern int Kill(int pid, int sig);
}

/// <summary>HTTP over a Unix socket.</summary>
public static class UnixHttp
{
    public static HttpClient Client(string socketPath) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { BaseAddress = new Uri("http://localhost") };
}
