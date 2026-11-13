using Sleipnir.Core.Api;

namespace Sleipnir.Server.Api;

internal static class ServerEndpoints
{
    public static void Map(IEndpointRouteBuilder api, Action requestShutdown)
    {
        var version = typeof(ServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        api.MapGet("/server", () => new ServerInfoDto("Sleipnir", version, ["v1"]));

        api.MapPost("/server/shutdown", () =>
        {
            requestShutdown();
            return Results.Accepted();
        });
    }
}
