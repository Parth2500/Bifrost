using Bifrost.Core.Api;

namespace Bifrost.Server.Api;

internal static class ServerEndpoints
{
    public static void Map(IEndpointRouteBuilder api, Action requestShutdown)
    {
        var version = typeof(ServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        api.MapGet("/server", () => new ServerInfoDto("Bifröst", version, ["v1"]));

        api.MapPost("/server/shutdown", () =>
        {
            requestShutdown();
            return Results.Accepted();
        }).AddEndpointFilter(RequireAdmin.Filter);
    }
}

internal static class RequireAdmin
{
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Principal().IsAdmin
            ? await next(context)
            : Problem.Create(403, "forbidden", "Only an admin can do that.");
}
