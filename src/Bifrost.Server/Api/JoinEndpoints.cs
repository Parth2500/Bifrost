using Bifrost.Server.People;

namespace Bifrost.Server.Api;

/// <summary>Pairing path B: a device asks to join, an admin compares the code and approves (design §7.2).</summary>
internal sealed class JoinEndpoints(JoinService? join, PeopleService people)
{
    public const string PublicPrefix = "/api/v1/join";

    private sealed record StartRequest(string? Name, string? Platform, string? Nonce);
    private sealed record CollectRequest(string? Nonce);
    private sealed record ApproveRequest(Guid PersonId);

    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/join", Start);
        api.MapPost("/join/{id:guid}/collect", Collect);
        api.MapGet("/join-requests", List);
        api.MapPost("/join-requests/{id:guid}/approve", Approve);
        api.MapDelete("/join-requests/{id:guid}", Reject);
    }

    private IResult? Off => join is null ? Problem.Create(409, "lan_off", "The home network listener is off.") : null;

    private async Task<IResult> Start(StartRequest body)
    {
        if (Off is { } off) return off;
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100 || (body.Platform?.Length ?? 0) > 50
            || body.Nonce is not { Length: >= 16 and <= 64 })
        {
            return Problem.Create(400, "invalid_request", "A device name and a nonce of 16 to 64 characters are required.");
        }
        if (join!.Start(body.Name.Trim(), body.Platform ?? "unknown", body.Nonce) is not { } started)
        {
            return Problem.Create(429, "too_many_requests", "Too many join requests are waiting.");
        }
        await Task.CompletedTask;
        return Results.Ok(new { requestId = started.Id, serverNonce = started.ServerNonce });
    }

    /// <summary>202 while the admin has not decided; 200 with the token once; 403 when refused.</summary>
    private async Task<IResult> Collect(Guid id, CollectRequest body)
    {
        if (Off is { } off) return off;
        var (outcome, device) = await join!.CollectAsync(id, body.Nonce ?? "");
        return outcome switch
        {
            JoinService.Outcome.Waiting => Results.Json(new { status = "waiting" }, statusCode: 202),
            JoinService.Outcome.Rejected => Problem.Create(403, "join_rejected", "The admin refused this device."),
            JoinService.Outcome.Approved => Results.Ok(new { token = device!.Token, deviceId = device.Device.Id }),
            _ => Problem.Create(401, "join_unknown", "Unknown, expired or collected request."), // counts as a failure
        };
    }

    private IResult List(HttpContext context) =>
        !context.Principal().IsAdmin ? Problem.Create(403, "forbidden", "Only an admin sees join requests.")
        : Off ?? Results.Ok(join!.Pending().Select(r => new { r.Id, r.Name, r.Platform, r.Code, expiresAt = r.Expires }));

    private async Task<IResult> Approve(HttpContext context, Guid id, ApproveRequest body)
    {
        if (!context.Principal().IsAdmin) return Problem.Create(403, "forbidden", "Only an admin can approve a device.");
        if (Off is { } off) return off;
        if (await people.GetPersonAsync(body.PersonId) is null) return Problem.Create(404, "person_not_found", "No such person.");
        return join!.Approve(id, body.PersonId) ? Results.NoContent() : Problem.Create(404, "join_not_found", "No such waiting request.");
    }

    private IResult Reject(HttpContext context, Guid id) =>
        !context.Principal().IsAdmin ? Problem.Create(403, "forbidden", "Only an admin can refuse a device.")
        : Off ?? (join!.Reject(id) ? Results.NoContent() : Problem.Create(404, "join_not_found", "No such waiting request."));
}
