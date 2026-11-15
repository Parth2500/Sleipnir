using Sleipnir.Core.Api;
using Sleipnir.Core.Interfaces;
using Sleipnir.Server.Identity;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Server.Api;

internal sealed class UserEndpoints(IdentityService identity, IDownloadService downloads)
{
    public void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/users").AddEndpointFilter(RequireAdmin.Filter);
        admin.MapGet("/", List);
        admin.MapPost("/", Create);
        admin.MapPatch("/{id:guid}", Update);
        admin.MapDelete("/{id:guid}", Delete);

        api.MapGet("/devices", ListDevices);
        api.MapDelete("/devices/{id:guid}", RevokeDevice);
    }

    internal static UserDto ToDto(UserEntity u) => new(u.Id, u.DisplayName, u.Role, u.CreatedAt, u.AllowedRoot);

    private static DeviceDto ToDto(DeviceEntity d) =>
        new(d.Id, d.UserId, d.Name, d.Platform, d.CreatedAt, d.LastSeenAt, d.RevokedAt is not null, d.IsLocal);

    private async Task<IResult> List() => Results.Ok((await identity.ListUsersAsync()).Select(ToDto));

    private Task<IResult> Create(CreateUserRequest request) => Guarded(async () =>
    {
        var user = await identity.CreateUserAsync(request.DisplayName ?? "", request.Role ?? Core.Models.UserRole.Member);
        return Results.Created($"/api/v1/users/{user.Id}", ToDto(user));
    });

    private Task<IResult> Update(Guid id, UpdateUserRequest request) => Guarded(async () =>
    {
        if (!PathRules.IsValidFolder(request.AllowedRoot))
        {
            return Problem.Create(400, "invalid_destination", "allowedRoot must be an absolute folder path.");
        }
        return Results.Ok(ToDto(await identity.UpdateUserAsync(id, request.DisplayName, request.Role, request.AllowedRoot)));
    });

    private Task<IResult> Delete(Guid id, string? confirm) => Guarded(async () =>
    {
        if (await identity.GetUserAsync(id) is null) throw new IdentityException("user_not_found");
        if (confirm != "true")
        {
            return Problem.Create(400, "confirmation_required", "Deleting a user cancels their downloads. Repeat with ?confirm=true.");
        }

        await identity.DeleteUserAsync(id); // refuses the last admin before anything else happens
        foreach (var download in downloads.GetAllDownloads().Where(d => d.OwnerId == id).ToList())
        {
            await downloads.CancelDownloadAsync(download.Id);
            await downloads.RemoveDownloadAsync(download.Id); // the files stay on disk
        }
        return Results.NoContent();
    });

    private async Task<IResult> ListDevices(HttpContext context, Guid? user)
    {
        var me = context.Principal();
        if (!me.IsAdmin && user is { } other && other != me.UserId)
        {
            return Problem.Create(403, "forbidden", "You can only list your own devices.");
        }
        var filter = me.IsAdmin ? user : me.UserId;
        return Results.Ok((await identity.ListDevicesAsync(filter)).Select(ToDto));
    }

    private async Task<IResult> RevokeDevice(HttpContext context, Guid id)
    {
        var me = context.Principal();
        var device = await identity.GetDeviceAsync(id);
        if (device is null || (!me.IsAdmin && device.UserId != me.UserId))
        {
            return Problem.Create(404, "device_not_found", "No such device.");
        }
        if (device.IsLocal)
        {
            return Problem.Create(409, "local_device", "This computer's device can't be revoked.");
        }
        await identity.RevokeDeviceAsync(id);
        return Results.NoContent();
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (IdentityException e)
        {
            return e.Code switch
            {
                "invalid_name" => Problem.Create(400, e.Code, "The name must be 1–40 characters without slashes or control characters."),
                "name_taken" => Problem.Create(409, e.Code, "Another user already has that name."),
                "last_admin" => Problem.Create(409, e.Code, "There must always be at least one admin."),
                "user_not_found" => Problem.Create(404, e.Code, "No such user."),
                _ => Problem.Create(400, e.Code, "The request was refused."),
            };
        }
    }
}

internal static class RequireAdmin
{
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Principal().IsAdmin
            ? await next(context)
            : Problem.Create(403, "forbidden", "Only an admin can do that.");
}
