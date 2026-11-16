using System.Text.RegularExpressions;
using Bifrost.Core.Api;
using Bifrost.Server.People;

namespace Bifrost.Server.Questions;

/// <summary>A refused question operation; <see cref="Code"/> is the stable API error code.</summary>
public sealed class QuestionException(string code, QuestionDto? question = null) : Exception(code)
{
    public string Code { get; } = code;

    /// <summary>For <c>already_closed</c>: how the question ended.</summary>
    public QuestionDto? Question { get; } = question;
}

/// <summary>
/// Asking the admin (design §5.1). Questions live in memory: they do not survive a restart, and the
/// extensions, which restart with Bifröst, ask again. Every change gets the next number of one
/// sequence, which askers and admins long-poll. The first answer wins, and is audited before it counts.
/// </summary>
public sealed partial class QuestionService(PeopleService people, TimeProvider time) : IDisposable
{
    /// <summary>The asker name of Bifröst's own questions.</summary>
    public const string Self = "bifrost";

    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private const int MaxOpenPerAsker = 200;
    private const int MaxClosedPerAsker = 200;

    /// <summary>How many questions are held, open and closed. For tests of the memory bound.</summary>
    public int Count
    {
        get { lock (_gate) return _questions.Count; }
    }
    private static readonly TimeSpan MinLife = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLife = TimeSpan.FromDays(7);
    private static readonly TimeSpan KeepClosed = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _questions = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;
    private bool _disposed;

    private sealed class Entry
    {
        public required QuestionDto Question { get; set; }
        public ITimer? Expiry { get; set; }
        public bool Answering { get; set; }
        public DateTime ClosedAt { get; set; }
    }

    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex ChoiceId();

    public QuestionDto Ask(string asker, string title, string text, IReadOnlyList<Choice> choices, DateTime expiresAt, bool online)
    {
        ArgumentException.ThrowIfNullOrEmpty(asker);
        var now = Now;
        expiresAt = expiresAt.ToUniversalTime();
        title = title?.Trim() ?? "";
        text = text?.Trim() ?? "";
        var valid = title.Length is >= 1 and <= 80 && !title.Any(char.IsControl)
            && text.Length <= 500 && !text.Any(c => char.IsControl(c) && c != '\n')
            && choices is { Count: >= 2 and <= 4 }
            && choices.All(c => c is { Id: not null, Label: not null } && ChoiceId().IsMatch(c.Id)
                && c.Label.Trim().Length is >= 1 and <= 40 && !c.Label.Any(char.IsControl))
            && choices.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == choices.Count
            && expiresAt - now >= MinLife && expiresAt - now <= MaxLife;
        if (!valid) throw new QuestionException("invalid_question");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var old in _questions.Where(q => q.Value.Question.State != QuestionState.Open && now - q.Value.ClosedAt > KeepClosed).Select(q => q.Key).ToList())
            {
                _questions.Remove(old);
            }
            // Closed questions are kept a while so the asker can read how they ended, but an asker that
            // asks and withdraws in a loop must not grow the home's memory: its oldest closed ones go first.
            foreach (var extra in _questions.Where(q => q.Value.Question.State != QuestionState.Open && q.Value.Question.Asker == asker)
                         .OrderByDescending(q => q.Value.Question.Seq).Skip(MaxClosedPerAsker - 1).Select(q => q.Key).ToList())
            {
                _questions.Remove(extra);
            }
            if (_questions.Values.Count(q => q.Question.State == QuestionState.Open && q.Question.Asker == asker) >= MaxOpenPerAsker)
            {
                throw new QuestionException("too_many_questions");
            }

            var id = Guid.NewGuid();
            var question = new QuestionDto(id, asker, title, text, [.. choices.Select(c => new Choice(c.Id, c.Label.Trim()))],
                expiresAt, online, QuestionState.Open, null, null, ++_seq);
            var entry = new Entry { Question = question };
            _questions[id] = entry;
            entry.Expiry = time.CreateTimer(_ => Expire(id), null, expiresAt - now, Timeout.InfiniteTimeSpan);
            Wake();
            return question;
        }
    }

    public async Task<QuestionDto> AnswerAsync(Guid id, string choice, Principal admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        if (!admin.IsAdmin) throw new QuestionException("forbidden");

        QuestionDto question;
        lock (_gate)
        {
            if (!_questions.TryGetValue(id, out var entry)) throw new QuestionException("question_not_found");
            question = entry.Question;
            // Someone else's answer is on its way to the audit log: theirs came first.
            if (question.State != QuestionState.Open || entry.Answering) throw new QuestionException("already_closed", question);
            if (!question.Choices.Any(c => c.Id == choice)) throw new QuestionException("invalid_choice");
            entry.Answering = true; // from here nothing else may close it
        }

        try
        {
            // Audited first: an answer that could not be recorded does not count.
            await people.RecordAsync(admin, "question.answered", id, $"{question.Asker}:{choice}");
        }
        catch
        {
            lock (_gate)
            {
                if (_questions.TryGetValue(id, out var entry))
                {
                    entry.Answering = false;
                    if (Now >= question.ExpiresAt) Close(entry, QuestionState.Expired, null, null);
                }
            }
            throw;
        }

        lock (_gate)
        {
            return _questions.TryGetValue(id, out var entry)
                ? Close(entry, QuestionState.Answered, choice, admin.PersonId)
                : question with { State = QuestionState.Answered, Choice = choice, AnsweredBy = admin.PersonId };
        }
    }

    /// <summary>The asker takes its question back. False when it is not this asker's, or no longer open.</summary>
    public bool Withdraw(Guid id, string asker)
    {
        lock (_gate)
        {
            if (!_questions.TryGetValue(id, out var entry) || entry.Question.Asker != asker) return false;
            if (entry.Question.State != QuestionState.Open || entry.Answering) return false;
            Close(entry, QuestionState.Withdrawn, null, null);
            return true;
        }
    }

    public QuestionDto? Get(Guid id)
    {
        lock (_gate) return _questions.GetValueOrDefault(id)?.Question;
    }

    public IReadOnlyList<QuestionDto> ListOpen()
    {
        lock (_gate)
        {
            return [.. _questions.Values.Select(e => e.Question).Where(q => q.State == QuestionState.Open).OrderBy(q => q.Seq)];
        }
    }

    public long Seq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>Returns the current change number: at once when it is past <paramref name="afterSeq"/>, else on the next change or after <paramref name="max"/>.</summary>
    public async Task<long> WaitForChangeAsync(long afterSeq, TimeSpan max, CancellationToken ct)
    {
        Task changed;
        lock (_gate)
        {
            if (_seq > afterSeq || max <= TimeSpan.Zero) return _seq;
            changed = _changed.Task;
        }
        try
        {
            await changed.WaitAsync(max < MaxWait ? max : MaxWait, time, ct);
        }
        catch (TimeoutException)
        {
            // Nothing changed; the caller asks again.
        }
        return Seq;
    }

    /// <summary>Waits until the question is no longer open, or <paramref name="max"/> has passed. Null when there is no such question.</summary>
    public async Task<QuestionDto?> WaitUntilClosedAsync(Guid id, TimeSpan max, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + (max < MaxWait ? max : MaxWait);
        while (true)
        {
            long seq;
            QuestionDto? question;
            lock (_gate)
            {
                seq = _seq;
                question = _questions.GetValueOrDefault(id)?.Question;
            }
            var left = deadline - time.GetUtcNow();
            if (question is not { State: QuestionState.Open } || left <= TimeSpan.Zero) return question;
            await WaitForChangeAsync(seq, left, ct);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _questions.Values) entry.Expiry?.Dispose();
            _questions.Clear();
            Wake();
        }
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Expire(Guid id)
    {
        lock (_gate)
        {
            if (_questions.TryGetValue(id, out var entry) && entry.Question.State == QuestionState.Open && !entry.Answering)
            {
                Close(entry, QuestionState.Expired, null, null);
            }
        }
    }

    /// <summary>Called under the gate.</summary>
    private QuestionDto Close(Entry entry, QuestionState state, string? choice, Guid? answeredBy)
    {
        entry.Expiry?.Dispose();
        entry.Expiry = null;
        entry.Answering = false;
        entry.ClosedAt = Now;
        entry.Question = entry.Question with { State = state, Choice = choice, AnsweredBy = answeredBy, Seq = ++_seq };
        Wake();
        return entry.Question;
    }

    /// <summary>Called under the gate.</summary>
    private void Wake()
    {
        var waiting = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.TrySetResult();
    }
}
