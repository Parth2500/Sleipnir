using System.Net;
using System.Text.Json;
using Sleipnir.Core.Models;

namespace Sleipnir.Tests.Server;

public class UserEndpointTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("code").GetString()!;

    private static string Body(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task The_admin_creates_a_member_who_can_then_use_the_api()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/users", Body(new { displayName = "Bob" })));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Bob", json.GetProperty("displayName").GetString());
        Assert.Equal("member", json.GetProperty("role").GetString());
        var users = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/users")));
        Assert.Equal(2, users.GetArrayLength());
    }

    [Fact]
    public async Task Bad_and_duplicate_names_are_refused()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/users", Body(new { displayName = "Bob" })));

        var dupe = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/users", Body(new { displayName = "bob" })));
        var bad = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/users", Body(new { displayName = "a/b" })));
        var missing = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/users", "{}"));

        Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);
        Assert.Equal("name_taken", await Code(dupe));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_name", await Code(bad));
        Assert.Equal("invalid_name", await Code(missing));
    }

    [Fact]
    public async Task Members_cannot_manage_users()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, token) = await daemon.AddUserAsync("bob");

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, "/api/v1/users", null),
            (HttpMethod.Post, "/api/v1/users", Body(new { displayName = "eve", role = "admin" })),
            (HttpMethod.Patch, $"/api/v1/users/{bob.UserId}", Body(new { role = "admin" })),
            (HttpMethod.Delete, $"/api/v1/users/{daemon.Admin.UserId}?confirm=true", null),
        })
        {
            var response = await daemon.Http.SendAsync(daemon.Request(method, path, body, token));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("forbidden", await Code(response));
        }

        Assert.Equal(UserRole.Member, (await daemon.Identity.GetUserAsync(bob.UserId))!.Role);
        Assert.Equal(2, (await daemon.Identity.ListUsersAsync()).Count);
    }

    [Fact]
    public async Task Users_can_be_renamed_promoted_and_given_a_folder()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, _) = await daemon.AddUserAsync("bob");

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Patch, $"/api/v1/users/{bob.UserId}",
            Body(new { displayName = "Robert", role = "admin", allowedRoot = "/srv/bob" })));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Robert", json.GetProperty("displayName").GetString());
        Assert.Equal("admin", json.GetProperty("role").GetString());
        Assert.Equal("/srv/bob", json.GetProperty("allowedRoot").GetString());
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("a\\u0000b")]
    public async Task An_allowed_root_must_be_an_absolute_path(string root)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, _) = await daemon.AddUserAsync("bob");

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Patch, $"/api/v1/users/{bob.UserId}",
            Body(new { allowedRoot = root })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_destination", await Code(response));
        Assert.Null((await daemon.Identity.GetUserAsync(bob.UserId))!.AllowedRoot);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_or_deleted()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var demote = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Patch, $"/api/v1/users/{daemon.Admin.UserId}", Body(new { role = "member" })));
        var delete = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/users/{daemon.Admin.UserId}?confirm=true"));

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await Code(demote));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("last_admin", await Code(delete));
        Assert.Empty(daemon.Downloads.Calls);
    }

    [Fact]
    public async Task Deleting_a_user_needs_confirmation_and_cancels_their_downloads_but_keeps_files()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, token) = await daemon.AddUserAsync("bob");
        var running = daemon.Downloads.Seed("busy.zip", DownloadStatus.Downloading);
        running.OwnerId = bob.UserId;
        var file = Path.Combine(Path.GetTempPath(), "sleipnir-keep-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "precious");
        var done = daemon.Downloads.Seed("done.zip", DownloadStatus.Completed);
        done.OwnerId = bob.UserId;
        done.DestinationPath = file;
        var others = daemon.Downloads.Seed("admin.zip", DownloadStatus.Downloading);
        try
        {
            var unconfirmed = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/users/{bob.UserId}"));
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            Assert.Equal("confirmation_required", await Code(unconfirmed));
            Assert.Equal(3, daemon.Downloads.Tasks.Count);

            var deleted = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/users/{bob.UserId}?confirm=true"));

            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(new[] { others.Id }, daemon.Downloads.Tasks.Select(t => t.Id));
            Assert.Contains("cancel busy.zip", daemon.Downloads.Calls);
            Assert.True(File.Exists(file));
            Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: token))).StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Deleting_an_unknown_user_is_a_404()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/users/{Guid.NewGuid()}?confirm=true"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("user_not_found", await Code(response));
    }

    [Fact]
    public async Task Devices_are_listed_per_user_for_members_and_for_everyone_for_admins()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, bobToken) = await daemon.AddUserAsync("bob");
        var (eve, _) = await daemon.AddUserAsync("eve");

        var mine = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/devices", token: bobToken)));
        var all = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/devices")));
        var filtered = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/devices?user={eve.UserId}")));
        var snoop = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/devices?user={eve.UserId}", token: bobToken));

        Assert.Equal(new[] { bob.DeviceId }, mine.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal(3, all.GetArrayLength());
        Assert.Equal(new[] { eve.DeviceId }, filtered.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, snoop.StatusCode);
    }

    [Fact]
    public async Task A_device_can_be_revoked_by_its_owner_or_an_admin_but_not_by_others()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, bobToken) = await daemon.AddUserAsync("bob");
        var (eve, eveToken) = await daemon.AddUserAsync("eve");

        var stranger = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/devices/{bob.DeviceId}", token: eveToken));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal("device_not_found", await Code(stranger));
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: bobToken))).StatusCode);

        var own = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/devices/{bob.DeviceId}", token: bobToken));
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: bobToken))).StatusCode);

        var byAdmin = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/devices/{eve.DeviceId}"));
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: eveToken))).StatusCode);
    }

    [Fact]
    public async Task The_local_device_cannot_be_revoked()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/devices/{daemon.Admin.DeviceId}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("local_device", await Code(response));
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me"))).StatusCode);
    }

    [Fact]
    public async Task Token_hashes_never_appear_in_responses()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, token) = await daemon.AddUserAsync("bob");
        var hash = Sleipnir.Server.Identity.IdentityService.HashToken(token);

        foreach (var path in new[] { "/api/v1/users", "/api/v1/devices", "/api/v1/me" })
        {
            var text = await (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, path))).Content.ReadAsStringAsync();
            Assert.DoesNotContain(hash, text);
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain("tokenHash", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
