using Bifrost.Core.Api;
using Bifrost.Server.People;

namespace Bifrost.Server.Api;

internal static class HttpContextPrincipal
{
    private const string Key = "bifrost.principal";

    public static void SetPrincipal(this HttpContext context, Principal principal) => context.Items[Key] = principal;

    /// <summary>The authenticated caller. Only valid on routes behind the authentication middleware.</summary>
    public static Principal Principal(this HttpContext context) => (Principal)context.Items[Key]!;
}

internal static class MeEndpoints
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/me", (HttpContext context) =>
        {
            var me = context.Principal();
            return new MeDto(me.PersonId, me.PersonName, me.Role, me.DeviceId, me.DeviceName, me.Kind);
        });
}
