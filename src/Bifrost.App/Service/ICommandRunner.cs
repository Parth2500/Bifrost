using System.Diagnostics;

namespace Bifrost.App.Service;

public interface ICommandRunner
{
    Task<(int Exit, string Output)> RunAsync(string file, params string[] args);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int Exit, string Output)> RunAsync(string file, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, (await output + await error).Trim());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, $"Could not run {file}: {e.Message}");
        }
    }
}
