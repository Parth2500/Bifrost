using Bifrost.Server.People;

namespace Bifrost.Server.Api;

/// <summary>Pairing path B: a device asks to join, an admin compares the code and approves (design §7.2).</summary>
internal sealed class JoinEndpoints(JoinService? join, PeopleService people, Bifrost.Server.Security.AuthFailureLimiter startLimiter)
{
    public const string PublicPrefix = "/api/v1/join";

    private sealed record StartRequest(string? Name, string? Platform, string? Commit);
    private sealed record CollectRequest(string? Nonce);
    private sealed record ApproveRequest(Guid PersonId);

    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/join", Start);
        api.MapPost("/join/{id:guid}/reveal", Reveal);
        api.MapPost("/join/{id:guid}/collect", Collect);
        api.MapGet("/join-requests", List);
        api.MapPost("/join-requests/{id:guid}/approve", Approve);
        api.MapDelete("/join-requests/{id:guid}", Reject);
    }

    private IResult? Off => join is null ? Problem.Create(409, "lan_off", "The home network listener is off.") : null;

    private IResult Start(HttpContext context, StartRequest body)
    {
        if (Off is { } off) return off;
        // Counted here, in the handler, so every spelling of the route is counted (a flood would hold the waiting places).
        var asker = Bifrost.Server.Hosting.BifrostHost.LimiterKey(context.Connection.RemoteIpAddress);
        if (startLimiter.IsBlocked(asker)) return Problem.Create(429, "too_many_attempts", "Too many join requests. Try again later.");
        startLimiter.RecordFailure(asker);
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100 || (body.Platform?.Length ?? 0) > 50
            || body.Commit is not { Length: 64 } || !body.Commit.All(Uri.IsHexDigit))
        {
            return Problem.Create(400, "invalid_request", "A device name and a commitment (SHA-256 of the nonce, as 64 hex characters) are required.");
        }
        if (join!.Start(body.Name.Trim(), body.Platform ?? "unknown", Convert.FromHexString(body.Commit),
                Bifrost.Server.Hosting.BifrostHost.LimiterKey(context.Connection.RemoteIpAddress),
                browser: Bifrost.Server.Hosting.BifrostHost.IsFallback(context)) is not { } started)
        {
            return Problem.Create(429, "too_many_requests", "A join request from this address, or too many overall, is already waiting.");
        }
        return Results.Ok(new { requestId = started.Id, serverNonce = started.ServerNonce });
    }

    /// <summary>After the server's nonce is known the device shows its own; only then the admin sees the code.</summary>
    private IResult Reveal(Guid id, CollectRequest body) =>
        Off ?? (join!.Reveal(id, body.Nonce ?? "") ? Results.NoContent() : Problem.Create(401, "join_unknown", "Unknown request or the nonce does not match the commitment."));

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
