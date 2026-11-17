using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bifrost.App;

/// <summary>
/// Announces the home as <c>_bifrost._tcp</c> through the operating system's responder (Avahi), while the
/// home network listener is on (design §6.3). The TXT record carries the home name, the API version, the
/// port and a short fingerprint prefix; nothing about people or content. Discovery is a convenience: when
/// Avahi is missing or fails, Bifröst says so and carries on, and a link or a typed address still works.
/// </summary>
public sealed partial class MdnsAnnouncer(string homeName, int port, string fingerprint, ILogger logger) : IDisposable
{
    public const string ServiceType = "_bifrost._tcp";
    private Process? _process;

    public static IReadOnlyList<string> Arguments(string homeName, int port, string fingerprint) =>
    [
        "--service", homeName, ServiceType, port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        $"name={homeName}", "api=v1", $"port={port.ToString(System.Globalization.CultureInfo.InvariantCulture)}", $"fp={fingerprint[..16]}",
    ];

    public void Start()
    {
        var info = new ProcessStartInfo("avahi-publish") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in Arguments(homeName, port, fingerprint)) info.ArgumentList.Add(argument);
        try
        {
            _process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            LogMissing(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "avahi-publish is not installed, so the home is not announced on the network. Type its address instead.")]
    private static partial void LogMissing(ILogger logger);

    public void Dispose()
    {
        if (_process is { HasExited: false } running)
        {
            try { running.Kill(); }
            catch (InvalidOperationException) { }
        }
        _process?.Dispose();
    }
}
