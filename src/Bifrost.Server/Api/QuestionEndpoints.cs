using Bifrost.Core.Api;
using Bifrost.Server.Extensions;
using Bifrost.Server.Questions;

namespace Bifrost.Server.Api;

/// <summary>
/// Asking the admin (design §5.1). Extensions ask, wait and withdraw on their own socket; admins see
/// and answer on the loopback API (paired devices) and the admin socket (the desk companion).
/// </summary>
public static class QuestionEndpoints
{
    /// <summary>Routes on the extension API socket. The socket says which extension is asking.</summary>
    public static void MapForExtensions(IEndpointRouteBuilder routes, QuestionService questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        routes.MapPost("/v1/questions", (HttpContext context, AskRequest request) => Guarded(() =>
        {
            var question = questions.Ask(context.Extension(), request.Title ?? "", request.Text ?? "", request.Choices ?? [],
                request.ExpiresAt ?? default, request.Online);
            return Task.FromResult(Results.Created($"/v1/questions/{question.Id}", question));
        }));

        routes.MapGet("/v1/questions/{id:guid}", async (HttpContext context, Guid id, int? wait) =>
        {
            // Another extension's question looks like a missing one.
            if (questions.Get(id) is not { } question || question.Asker != context.Extension()) return NotFound();
            var closed = await questions.WaitUntilClosedAsync(id, Seconds(wait), context.RequestAborted);
            return closed is null ? NotFound() : Results.Ok(closed);
        });

        routes.MapDelete("/v1/questions/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (questions.Get(id) is not { } question || question.Asker != context.Extension()) return NotFound();
            questions.Withdraw(id, context.Extension()); // already closed is fine: it is gone either way
            return Results.NoContent();
        });
    }

    /// <summary>Routes under <c>/api/v1</c>, for admins only.</summary>
    internal static void MapForAdmins(IEndpointRouteBuilder api, QuestionService questions)
    {
        var admin = api.MapGroup("/questions").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Principal().IsAdmin ? await next(context) : Problem.Create(403, "forbidden", "Only an admin answers questions."));

        admin.MapGet("/", async (HttpContext context, long? after, int? wait) =>
        {
            if (after is { } seq) await questions.WaitForChangeAsync(seq, Seconds(wait), context.RequestAborted);
            // The number is read before the list, so a change in between is seen again, never missed.
            var now = questions.Seq;
            return Results.Ok(new QuestionListDto(now, questions.ListOpen()));
        });

        admin.MapPost("/{id:guid}/answer", (HttpContext context, Guid id, AnswerRequest request) => Guarded(async () =>
            Results.Ok(await questions.AnswerAsync(id, request.Choice ?? "", context.Principal()))));
    }

    private static TimeSpan Seconds(int? wait) => TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, (int)QuestionService.MaxWait.TotalSeconds));

    private static IResult NotFound() => Problem.Create(404, "question_not_found", "No such question.");

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (QuestionException e)
        {
            return e.Code switch
            {
                "invalid_question" => Problem.Create(400, e.Code, "A question needs a title, 2 to 4 choices and an expiry between a minute and a week ahead."),
                "too_many_questions" => Problem.Create(429, e.Code, "Too many questions are open."),
                "invalid_choice" => Problem.Create(400, e.Code, "That is not one of the question's choices."),
                "question_not_found" => NotFound(),
                "forbidden" => Problem.Create(403, e.Code, "Only an admin answers questions."),
                // The loser of a race learns how it ended.
                "already_closed" => Results.Json(
                    new ClosedProblem("about:blank", "The question is already closed.", 409, e.Code, e.Question),
                    contentType: "application/problem+json", statusCode: 409),
                _ => throw new InvalidOperationException("Unmapped question error.", e),
            };
        }
    }

    private sealed record ClosedProblem(string Type, string Title, int Status, string Code, QuestionDto? Question);
}
