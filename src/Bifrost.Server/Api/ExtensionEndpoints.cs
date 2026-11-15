using Bifrost.Core.Api;
using Bifrost.Server.Extensions;

namespace Bifrost.Server.Api;

/// <summary>What the admin sees of the extensions.</summary>
internal static class ExtensionEndpoints
{
    public static void Map(IEndpointRouteBuilder api, ExtensionManager? extensions) =>
        api.MapGet("/extensions", () => Results.Ok((extensions?.All ?? []).Select(s =>
                new ExtensionDto(s.Name, s.State.ToString().ToLowerInvariant(), s.Pid, s.Restarts))))
            .AddEndpointFilter(RequireAdmin.Filter);
}
