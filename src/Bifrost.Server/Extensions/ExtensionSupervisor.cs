using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Bifrost.Server.Extensions;

/// <summary>
/// One extension's child process (design §8.4): started under Bifröst's own account with only the
/// contract's environment, ready once its private socket accepts a connection, restarted with a
/// growing delay when it exits, given up after repeated crashes, and stopped with SIGTERM.
/// </summary>
public sealed partial class ExtensionSupervisor : IAsyncDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly TimeSpan ReadyLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);
    private static readonly string[] PassThrough = ["PATH", "LANG", "LC_ALL", "TZ"];

    private readonly string _program;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly IReadOnlyDictionary<string, string>? _extraEnvironment;
    private readonly RestartPolicy _policy = new();
    private readonly Lock _gate = new();
    private Process? _process;
    private DateTimeOffset _startedAt;
    private ITimer? _restart;
    private bool _stopping;
    private int _starts;

    /// <param name="extraEnvironment">Added to the child's environment; for tests only (the fake extension's mode).</param>
    public ExtensionSupervisor(string name, string program, string runtimeRoot, string dataRoot, TimeProvider time, ILogger log,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        Name = name;
        _program = program;
        _time = time;
        _log = log;
        _extraEnvironment = extraEnvironment;
        RuntimeRoot = runtimeRoot;
        SocketPath = Path.Combine(runtimeRoot, "x", name, "extension.sock");
        ApiSocketPath = Path.Combine(runtimeRoot, "x", name, "bifrost.sock");
        DataDir = Path.Combine(dataRoot, "x", name);
    }

    public string Name { get; }
    public string RuntimeRoot { get; }
    public string SocketPath { get; }
    public string ApiSocketPath { get; }
    public string DataDir { get; }
    public ExtensionState State { get; private set; } = ExtensionState.Stopped;
    public int? Pid { get; private set; }

    /// <summary>Starts after the first one.</summary>
    public int Restarts => Math.Max(0, Volatile.Read(ref _starts) - 1);

    public Task StartAsync()
    {
        foreach (var dir in new[] { Path.Combine(RuntimeRoot, "x"), Path.GetDirectoryName(SocketPath)!, DataDir })
        {
            Directory.CreateDirectory(dir, OwnerOnly);
            if (File.GetUnixFileMode(dir) != OwnerOnly) File.SetUnixFileMode(dir, OwnerOnly);
        }
        lock (_gate)
        {
            _stopping = false;
            _policy.Reset();
        }
        Launch();
        return Task.CompletedTask;
    }

    private void Launch()
    {
        Process process;
        lock (_gate)
        {
            if (_stopping) return;
            // A socket file left by a crash is not the extension listening.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);

            var info = new ProcessStartInfo(_program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = DataDir,
            };
            info.ArgumentList.Add("extension");
            info.Environment.Clear();
            foreach (var name in PassThrough)
            {
                if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            }
            foreach (var (key, value) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                         .Select(e => ((string)e.Key, (string?)e.Value)))
            {
                if (key.StartsWith("DOTNET_", StringComparison.Ordinal) && value is not null) info.Environment[key] = value;
            }
            info.Environment["HOME"] = DataDir;
            info.Environment["BIFROST_SOCKET"] = SocketPath;
            info.Environment["BIFROST_API_SOCKET"] = ApiSocketPath;
            info.Environment["BIFROST_DATA_DIR"] = DataDir;
            info.Environment["BIFROST_EXTENSION"] = Name;
            info.Environment["BIFROST_INTERFACE"] = "1";
            foreach (var (key, value) in _extraEnvironment ?? new Dictionary<string, string>()) info.Environment[key] = value;

            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogOutput(_log, Name, e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogOutput(_log, Name, e.Data); };
            process.Exited += (_, _) => OnExited(process);
            try
            {
                process.Start();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LogCouldNotStart(_log, Name, _program, e.Message);
                process.Dispose();
                State = ExtensionState.Failed;
                return;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _startedAt = _time.GetUtcNow();
            Pid = process.Id;
            Interlocked.Increment(ref _starts);
            State = ExtensionState.Starting;
        }
        _ = WaitUntilReadyAsync(process);
    }

    private async Task WaitUntilReadyAsync(Process process)
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            lock (_gate)
            {
                if (_process != process || State != ExtensionState.Starting) return;
            }
            if (await AcceptsConnectionAsync())
            {
                lock (_gate)
                {
                    if (_process == process && State == ExtensionState.Starting) State = ExtensionState.Running;
                }
                return;
            }
            if (_time.GetElapsedTime(started) >= ReadyLimit)
            {
                LogNotReady(_log, Name, ReadyLimit.TotalSeconds);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return; // the exit is counted as a crash
            }
            await Task.Delay(100);
        }
    }

    private async Task<bool> AcceptsConnectionAsync()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void OnExited(Process process)
    {
        lock (_gate)
        {
            if (_process != process) return;
            var code = SafeExitCode(process);
            _process = null;
            Pid = null;
            if (_stopping)
            {
                State = ExtensionState.Stopped;
                return;
            }

            var delay = _policy.OnExit(_startedAt, _time.GetUtcNow());
            if (delay is not { } wait)
            {
                State = ExtensionState.Failed;
                LogGaveUp(_log, Name, code);
                return;
            }
            State = ExtensionState.Crashed;
            LogExited(_log, Name, code, wait.TotalSeconds);
            _restart?.Dispose();
            _restart = _time.CreateTimer(_ => Launch(), null, wait, Timeout.InfiniteTimeSpan);
        }
        process.Dispose();
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            _stopping = true;
            _restart?.Dispose();
            _restart = null;
            process = _process;
            if (process is null)
            {
                State = ExtensionState.Stopped;
                return;
            }
        }

        try
        {
            if (!process.HasExited) _ = NativeMethods.Kill(process.Id, NativeMethods.SIGTERM);
            using var grace = new CancellationTokenSource(StopGrace);
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        lock (_gate)
        {
            _process = null;
            Pid = null;
            State = ExtensionState.Stopped;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Name}] {Line}")]
    private static partial void LogOutput(ILogger logger, string name, string line);

    [LoggerMessage(Level = LogLevel.Error, Message = "Extension {Name} could not start {Program}: {Reason}")]
    private static partial void LogCouldNotStart(ILogger logger, string name, string program, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extension {Name} did not open its socket within {Seconds} s; stopping it")]
    private static partial void LogNotReady(ILogger logger, string name, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extension {Name} exited (code {Code}); starting it again in {Seconds} s")]
    private static partial void LogExited(ILogger logger, string name, int? code, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Extension {Name} exited (code {Code}) too often; not starting it again")]
    private static partial void LogGaveUp(ILogger logger, string name, int? code);
}

internal static class NativeMethods
{
    public const int SIGTERM = 15;

    // Plain ints only, so no marshalling; LibraryImport would need unsafe code for nothing.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static extern int Kill(int pid, int signal);
}
