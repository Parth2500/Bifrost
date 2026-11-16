using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Core.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.App.Desk;

/// <summary>
/// The desk companion's work (design §5.1): long-poll the open questions on the admin socket, show
/// each one once, take down those that closed elsewhere, and send the admin's click as the answer.
/// Bifröst may be down, starting, or restarting under it: it keeps trying, with a growing delay.
/// </summary>
public sealed partial class DeskLoop(HttpClient adminSocket, INotifier notifier, TimeProvider time, ILogger? logger = null)
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public async Task RunAsync(CancellationToken ct)
    {
        // Every question on screen or already dismissed, so none is shown twice.
        var shown = new Dictionary<Guid, CancellationTokenSource>();
        var retry = TimeSpan.FromSeconds(1);
        long? seq = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QuestionListDto list;
                try
                {
                    list = await FetchAsync(seq, ct);
                    retry = TimeSpan.FromSeconds(1);
                }
                catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                    || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    LogUnreachable(_log, e.Message, retry.TotalSeconds);
                    seq = null; // a restarted Bifröst counts from the start again
                    await Task.Delay(retry, time, ct);
                    retry = retry * 2 < MaxRetryDelay ? retry * 2 : MaxRetryDelay;
                    continue;
                }

                seq = list.Seq;
                foreach (var question in list.Items.Where(q => !shown.ContainsKey(q.Id)))
                {
                    var close = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    shown[question.Id] = close;
                    _ = ShowAndAnswerAsync(question, close.Token);
                }
                foreach (var id in shown.Keys.Where(id => list.Items.All(q => q.Id != id)).ToList())
                {
                    // Answered elsewhere, withdrawn or expired: off the screen.
                    await shown[id].CancelAsync();
                    shown[id].Dispose();
                    shown.Remove(id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            foreach (var close in shown.Values)
            {
                await close.CancelAsync();
                close.Dispose();
            }
        }
    }

    private async Task<QuestionListDto> FetchAsync(long? after, CancellationToken ct)
    {
        var path = after is { } seq ? $"/api/v1/questions?after={seq}&wait=30" : "/api/v1/questions";
        using var response = await adminSocket.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException("this account is not Bifröst's admin");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<QuestionListDto>(Json, ct) ?? throw new JsonException("empty answer");
    }

    private async Task ShowAndAnswerAsync(QuestionDto question, CancellationToken closeWhen)
    {
        try
        {
            var choice = await notifier.ShowAsync(question.Id, question.Title, question.Text, question.Choices, closeWhen);
            if (choice is null) return;

            // Not tied to closeWhen: a click that raced the close still gets its honest "already closed".
            using var response = await adminSocket.PostAsJsonAsync($"/api/v1/questions/{question.Id}/answer", new AnswerRequest(choice), Json, CancellationToken.None);
            if (response.StatusCode == HttpStatusCode.Conflict) LogTooLate(_log, question.Id);
            else if (!response.IsSuccessStatusCode) LogAnswerFailed(_log, question.Id, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            // Closed elsewhere while it was on screen.
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            LogAnswerFailed(_log, question.Id, e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot reach Bifröst ({Reason}); trying again in {Seconds} s")]
    private static partial void LogUnreachable(ILogger logger, string reason, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Question {Id} was already closed when the answer arrived")]
    private static partial void LogTooLate(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not show or answer question {Id}: {Reason}")]
    private static partial void LogAnswerFailed(ILogger logger, Guid id, string reason);
}
