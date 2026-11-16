namespace Bifrost.Core.Api;

/// <summary>One answer the admin can pick.</summary>
public sealed record Choice(string Id, string Label);

public enum QuestionState
{
    Open,
    Answered,
    Expired,
    Withdrawn,
}

/// <summary>A question for the admin (design §5.1). Only an <c>open</c> one can change.</summary>
/// <param name="Asker">An extension's name, or <c>bifrost</c>.</param>
/// <param name="Online">May also go to Huginn once the home is linked (step 7).</param>
/// <param name="Choice">The chosen choice's id, when answered.</param>
/// <param name="AnsweredBy">The person who answered.</param>
/// <param name="Seq">The change number at which this question last changed.</param>
public sealed record QuestionDto(
    Guid Id, string Asker, string Title, string Text, IReadOnlyList<Choice> Choices, DateTime ExpiresAt, bool Online,
    QuestionState State, string? Choice, Guid? AnsweredBy, long Seq);

/// <summary>The open questions, and the change number to long-poll after.</summary>
public sealed record QuestionListDto(long Seq, IReadOnlyList<QuestionDto> Items);

public sealed record AskRequest(string? Title, string? Text, IReadOnlyList<Choice>? Choices, DateTime? ExpiresAt, bool Online = false);

public sealed record AnswerRequest(string? Choice);
