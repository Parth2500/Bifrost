using Bifrost.Core.Models;
using Bifrost.Server.People;
using Bifrost.Server.Security;

namespace Bifrost.Server.Api;

/// <summary>Pairing path A: an admin invites a person, a device redeems the secret (design §7.1).</summary>
internal sealed class PairingEndpoints(PairingService pairing, PeopleService people, string? fingerprint, int? lanPort, string homeName)
{
    public const string PairPath = "/api/v1/pair";

    private sealed record InviteRequest(Guid PersonId);
    private sealed record PairRequest(string? Secret, string? Name, string? Platform);

    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/invitations", Invite);
        api.MapPost("/pair", Pair);
    }

    private async Task<IResult> Invite(HttpContext context, InviteRequest body, string? qr)
    {
        if (!context.Principal().IsAdmin) return Problem.Create(403, "forbidden", "Only an admin can invite a device.");
        if (fingerprint is null || lanPort is null) return Problem.Create(409, "lan_off", "The home network listener is off.");
        if (await people.GetPersonAsync(body.PersonId) is null) return Problem.Create(404, "person_not_found", "No such person.");
        if (pairing.Invite(body.PersonId) is not { } invitation) return Problem.Create(429, "too_many_invitations", "Too many open invitations.");

        // The address the admin used to reach this, which a device can reach too, unless it was loopback:
        // then this machine's own address on the home network.
        var host = context.Request.Host.Host;
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing to put in the link if this machine's address cannot be found: a loopback address would never work from a phone.
            host = HostAddresses.Lan() ?? "";
            if (host.Length == 0) return Problem.Create(409, "no_lan_address", "This machine has no address on the home network. Connect it to the Wi-Fi or network and try again.");
        }
        var link = $"bifrost://pair?h={Uri.EscapeDataString($"{host}:{lanPort}")}&fp={fingerprint}"
            + $"&t={invitation.Secret}&n={Uri.EscapeDataString(homeName)}";
        // The host screen asks for the code as a picture (?qr=svg), so it needs no code of its own to draw one.
        string? qrSvg = null;
        if (qr == "svg")
        {
            using var data = new QRCoder.QRCodeGenerator().CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
            qrSvg = new QRCoder.SvgQRCode(data).GetGraphic(8, "#111820", "#ffffff", drawQuietZones: true, sizingMode: QRCoder.SvgQRCode.SizingMode.ViewBoxAttribute);
        }
        return Results.Ok(new { link, expiresAt = invitation.Expires, qrSvg });
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
