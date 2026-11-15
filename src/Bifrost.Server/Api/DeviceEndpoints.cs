using Bifrost.Core.Api;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;

namespace Bifrost.Server.Api;

/// <summary>A person sees and revokes their own devices; an admin, anyone's (design §5).</summary>
internal sealed class DeviceEndpoints(PeopleService people)
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/devices", List);
        api.MapDelete("/devices/{id:guid}", Revoke);
    }

    private static DeviceDto ToDto(DeviceEntity d) =>
        new(d.Id, d.PersonId, d.Name, d.Platform, d.Kind, d.CreatedAt, d.LastSeenAt, d.RevokedAt is not null, d.IsLocal);

    private async Task<IResult> List(HttpContext context, Guid? person)
    {
        var me = context.Principal();
        if (!me.IsAdmin && person is { } other && other != me.PersonId)
        {
            return Problem.Create(403, "forbidden", "You can only list your own devices.");
        }
        return Results.Ok((await people.ListDevicesAsync(me.IsAdmin ? person : me.PersonId)).Select(ToDto));
    }

    private async Task<IResult> Revoke(HttpContext context, Guid id)
    {
        var me = context.Principal();
        var device = await people.GetDeviceAsync(id);
        if (device is null || (!me.IsAdmin && device.PersonId != me.PersonId))
        {
            return Problem.Create(404, "device_not_found", "No such device."); // another's device looks like a missing one
        }
        if (device.IsLocal)
        {
            return Problem.Create(409, "local_device", "This computer's device can't be revoked.");
        }
        await people.RevokeDeviceAsync(me, id);
        return Results.NoContent();
    }
}
