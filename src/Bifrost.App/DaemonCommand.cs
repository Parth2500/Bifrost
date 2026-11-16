using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Bifrost.Server.Api;
using Bifrost.Server.Extensions;
using Bifrost.Server.Hosting;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Bifrost.Server.Questions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bifrost.App;

/// <summary><c>bifrost daemon</c>: the home, with its loopback API and its extensions.</summary>
internal static class DaemonCommand
{
    /// <summary>Exit code for "Bifröst could not start" (sysexits EX_IOERR); 1 means another instance is running.</summary>
    public const int StartFailureExitCode = 74;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static async Task<int> RunAsync()
    {
        try
        {
            return await RunCoreAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DbUpdateException or SqliteException)
        {
            await Console.Error.WriteLineAsync($"Bifröst could not start: {e.Message}");
            return StartFailureExitCode;
        }
    }

    private static async Task<int> RunCoreAsync()
    {
        var paths = AppPaths.ForCurrentProcess();
        // The lock lives next to the data it protects, so it holds even when two processes disagree
        // about the runtime folder.
        using var instanceLock = InstanceLock.TryAcquire(paths.Data);
        if (instanceLock is null)
        {
            await Console.Error.WriteLineAsync(DaemonInfo.TryRead(paths.Runtime) is { } running
                ? $"Bifröst is already running (pid {running.Pid}, port {running.Port})."
                : "Bifröst is already running.");
            return 1;
        }

        using var stop = new CancellationTokenSource();
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);

        // systemd creates the runtime folder 0755; nothing in it is for other accounts.
        Directory.CreateDirectory(paths.Runtime, OwnerOnly);
        if (File.GetUnixFileMode(paths.Runtime) != OwnerOnly) File.SetUnixFileMode(paths.Runtime, OwnerOnly);
        // The token file of step 1 is gone; one left by an older version must not stay readable.
        File.Delete(Path.Combine(paths.Runtime, "admin.token"));

        var admin = AdminSettings.Load(paths.Settings);
        using var database = Database.OpenFile(Path.Combine(paths.Data, "bifrost.db"));
        await Database.MigrateAsync(database);
        using var people = new PeopleService(database, TimeProvider.System);
        await people.EnsureLocalAdminAsync(admin.Name);

        using var logFactory = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
        using var logs = new FactoryLogs(logFactory);
        // The service's unit hides /home, so programs there are refused. INVOCATION_ID alone does not
        // say "service": a desktop session passes its own down to every terminal. SYSTEMD_EXEC_PID
        // names the one process systemd itself started.
        var asService = Environment.GetEnvironmentVariable("SYSTEMD_EXEC_PID") == Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var programs = ExtensionSettings.Load(Path.Combine(paths.Settings, ExtensionSettings.FileName), asService,
            logFactory.CreateLogger("Bifrost.Extensions"));
        await using var extensions = new ExtensionManager(programs, paths.Runtime, paths.Data, TimeProvider.System, logs);
        using var questions = new QuestionService(people, TimeProvider.System);
        await using var extensionApi = new ExtensionApiHost(extensions.All, people, routes => QuestionEndpoints.MapForExtensions(routes, questions));
        // A Unix socket's path is limited to 108 bytes; say so instead of failing deep inside the listener.
        if (extensions.All.SelectMany(e => new[] { e.SocketPath, e.ApiSocketPath }).Append(paths.AdminSocket)
                .FirstOrDefault(p => Encoding.UTF8.GetByteCount(p) > 107) is { } tooLong)
        {
            throw new IOException($"the runtime folder's path is too long for a socket ({tooLong}).");
        }

        await using var host = BifrostHost.Create(new BifrostHostOptions(Admin: new AdminSocketOptions(paths.AdminSocket, admin.Uid)), people, TimeProvider.System, extensions: extensions, questions: questions);
        await host.StartAsync();
        try
        {
            // Bifröst's own listener first, then the socket extensions ask on, then the extensions.
            await extensionApi.StartAsync();
            await extensions.StartAllAsync();
            DaemonInfo.Write(paths.Runtime, new DaemonInfo(host.Port, Environment.ProcessId, Version));
            Console.WriteLine($"Bifröst listening on http://127.0.0.1:{host.Port} and, for {admin.Name}, on {paths.AdminSocket}");
            try
            {
                await host.WaitForShutdownAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Signalled: fall through to the same clean stop as the shutdown endpoint.
            }
        }
        finally
        {
            DaemonInfo.Delete(paths.Runtime);
            // No new requests, then the extensions, then (on dispose) the socket they ask on.
            await host.StopAsync();
            await extensions.StopAllAsync();
        }
        return 0;
    }

    private static string Version => typeof(DaemonCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Hands the console log to the parts that take a provider.</summary>
    private sealed class FactoryLogs(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

        public void Dispose()
        {
            // The factory is owned, and disposed, by the daemon.
        }
    }
}
