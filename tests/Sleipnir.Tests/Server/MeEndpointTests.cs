using System.Net;
using System.Text.Json;
using Sleipnir.Core.Models;

namespace Sleipnir.Tests.Server;

public class MeEndpointTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task The_local_token_is_the_admin_on_this_computer()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me"));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("admin", json.GetProperty("userName").GetString());
        Assert.Equal("admin", json.GetProperty("role").GetString());
        Assert.Equal("This computer", json.GetProperty("deviceName").GetString());
        Assert.Equal(daemon.Admin.UserId, json.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task A_member_token_is_a_member()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, token) = await daemon.AddUserAsync("bob");

        var json = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: token)));

        Assert.Equal("bob", json.GetProperty("userName").GetString());
        Assert.Equal("member", json.GetProperty("role").GetString());
        Assert.Equal(bob.DeviceId, json.GetProperty("deviceId").GetGuid());
    }

    [Fact]
    public async Task Me_needs_a_token()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_is_rejected_on_its_next_request()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, token) = await daemon.AddUserAsync("bob");
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: token))).StatusCode);

        await daemon.Identity.RevokeDeviceAsync(bob.DeviceId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: token))).StatusCode);
    }

    [Fact]
    public async Task A_deleted_user_is_rejected_on_the_next_request()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, token) = await daemon.AddUserAsync("bob");

        await daemon.Identity.DeleteUserAsync(bob.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: token))).StatusCode);
    }
}
