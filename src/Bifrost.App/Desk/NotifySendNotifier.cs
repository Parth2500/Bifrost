using System.Diagnostics;
using System.Globalization;
using Bifrost.Core.Api;

namespace Bifrost.App.Desk;

/// <summary>
/// Desktop notifications through <c>notify-send</c> (libnotify 0.8 or later, for action buttons) and
/// <c>gdbus</c> to take one down. Every argument goes through <c>ArgumentList</c>, never a shell.
/// </summary>
public sealed class NotifySendNotifier : INotifier
{
    public async Task<string?> ShowAsync(Guid questionId, string title, string text, IReadOnlyList<Choice> choices, CancellationToken closeWhen)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(text);
        var info = new ProcessStartInfo("notify-send") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in Arguments(title, text, choices)) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new IOException("notify-send did not start.");
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // never let a full pipe block it
        uint? id = null;
        try
        {
            // With --print-id the first line is the notification's id; a clicked action's name follows.
            if (uint.TryParse(await process.StandardOutput.ReadLineAsync(closeWhen), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                id = parsed;
            }
            var rest = await process.StandardOutput.ReadToEndAsync(closeWhen);
            await process.WaitForExitAsync(closeWhen);
            var action = rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return choices.Any(c => c.Id == action) ? action : null; // anything else is a dismissal
        }
        catch (OperationCanceledException)
        {
            if (id is { } shown) await CloseAsync(shown);
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>The arguments for <c>notify-send</c>. The text is escaped: a notification body may be read as markup.</summary>
    public static IReadOnlyList<string> Arguments(string title, string text, IReadOnlyList<Choice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(text);
        var body = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        // "--" ends the options, so a title that starts with a dash is still a title.
        // Critical and never expiring: a question waits on screen for its answer. At normal urgency a
        // desktop (GNOME) takes the banner away after a few seconds and reports it closed.
        return ["--app-name=Bifröst", "--urgency=critical", "--expire-time=0", "--print-id", "--wait", .. choices.Select(c => $"--action={c.Id}={c.Label}"), "--", title, body];
    }

    private static async Task CloseAsync(uint id)
    {
        var info = new ProcessStartInfo("gdbus") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[]
        {
            "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.CloseNotification", id.ToString(CultureInfo.InvariantCulture),
        })
        {
            info.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(info);
            if (process is not null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException)
        {
            // The notification stays up; a click on it gets "already closed" and does nothing.
        }
    }
}
