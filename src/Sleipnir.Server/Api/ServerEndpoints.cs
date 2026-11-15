using Sleipnir.Core.Api;
using Sleipnir.Server.Access;

namespace Sleipnir.Server.Api;

internal static class ServerEndpoints
{
    public static void Map(IEndpointRouteBuilder api, string mode, Action requestShutdown)
    {
        var version = typeof(ServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        api.MapGet("/server", () => new ServerInfoDto("Sleipnir", version, ["v1"], mode));

        api.MapPost("/server/shutdown", () =>
        {
            requestShutdown();
            return Results.Accepted();
        }).AddEndpointFilter(RequireAdmin.Filter); // spec §8.2: admin only
    }
}

internal static class RequireAdmin
{
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Caller().IsAdmin
            ? await next(context)
            : Problem.Create(403, "forbidden", "Only an admin can do that.");
}
