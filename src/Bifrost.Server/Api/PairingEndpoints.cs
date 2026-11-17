using Bifrost.Core.Models;
using Bifrost.Server.People;

namespace Bifrost.Server.Api;

/// <summary>Pairing path A: an admin invites a person, a device redeems the secret (design §7.1).</summary>
internal sealed class PairingEndpoints(PairingService pairing, PeopleService people, string? fingerprint, int? lanPort)
{
    public const string PairPath = "/api/v1/pair";

    private sealed record InviteRequest(Guid PersonId);
    private sealed record PairRequest(string? Secret, string? Name, string? Platform);

    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/invitations", Invite);
        api.MapPost("/pair", Pair);
    }

    private async Task<IResult> Invite(HttpContext context, InviteRequest body)
    {
        if (!context.Principal().IsAdmin) return Problem.Create(403, "forbidden", "Only an admin can invite a device.");
        if (fingerprint is null || lanPort is null) return Problem.Create(409, "lan_off", "The home network listener is off.");
        if (await people.GetPersonAsync(body.PersonId) is null) return Problem.Create(404, "person_not_found", "No such person.");
        if (pairing.Invite(body.PersonId) is not { } invitation) return Problem.Create(429, "too_many_invitations", "Too many open invitations.");

        // The address is the one the admin used to reach this, so it is a name the device can reach too.
        var link = $"bifrost://pair?h={Uri.EscapeDataString($"{context.Request.Host.Host}:{lanPort}")}&fp={fingerprint}"
            + $"&t={invitation.Secret}&n={Uri.EscapeDataString(Environment.MachineName)}";
        return Results.Ok(new { link, expiresAt = invitation.Expires });
    }

    /// <summary>Public: the secret is the credential. Wrong secrets count against the caller in the host's limiter.</summary>
    private async Task<IResult> Pair(PairRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Secret) || string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100
            || (body.Platform?.Length ?? 0) > 50)
        {
            return Problem.Create(400, "invalid_request", "A secret and a device name are required.");
        }
        if (await pairing.RedeemAsync(body.Secret, body.Name.Trim(), body.Platform ?? "unknown") is not { } issued)
        {
            return Problem.Create(401, "invitation_invalid", "The invitation is unknown, used or expired.");
        }
        return Results.Ok(new { token = issued.Token, deviceId = issued.Device.Id });
    }
}
