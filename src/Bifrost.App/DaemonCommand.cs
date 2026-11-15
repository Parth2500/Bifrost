using System.Runtime.InteropServices;
using Bifrost.Server.Hosting;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;
using Bifrost.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.App;

/// <summary><c>bifrost daemon</c>: the home, with its loopback API.</summary>
internal static class DaemonCommand
{
    /// <summary>Exit code for "Bifröst could not start" (sysexits EX_IOERR); 1 means another instance is running.</summary>
    public const int StartFailureExitCode = 74;

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

        var token = LocalAdminToken.LoadOrCreate(paths.Runtime);
        using var database = Database.OpenFile(Path.Combine(paths.Data, "bifrost.db"));
        await Database.MigrateAsync(database);
        using var people = new PeopleService(database, TimeProvider.System);
        await people.EnsureLocalAdminAsync(Environment.UserName, token);

        await using var host = BifrostHost.Create(new BifrostHostOptions(), people, TimeProvider.System);
        await host.StartAsync();
        try
        {
            DaemonInfo.Write(paths.Runtime, new DaemonInfo(host.Port, Environment.ProcessId, Version));
            Console.WriteLine($"Bifröst listening on http://127.0.0.1:{host.Port}");
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
            await host.StopAsync();
        }
        return 0;
    }

    private static string Version => typeof(DaemonCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
