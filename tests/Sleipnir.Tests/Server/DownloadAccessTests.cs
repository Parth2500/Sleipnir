using System.Net;
using System.Text.Json;
using Sleipnir.Core.Models;

namespace Sleipnir.Tests.Server;

public class DownloadAccessTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("code").GetString()!;

    private static string Add(string url, string? destination = null) =>
        JsonSerializer.Serialize(new { urls = new[] { url }, destination });

    private static async Task<List<Guid>> ListIds(DaemonFixture daemon, string token, string query = "")
    {
        var json = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/downloads" + query, token: token)));
        return json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private static async Task<Guid> AddAs(DaemonFixture daemon, string token, string url)
    {
        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add(url), token));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("items")[0].GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Each_member_lists_only_their_own_downloads_and_the_admin_lists_all()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");
        var (_, eveToken) = await daemon.AddUserAsync("eve");
        var bobs = await AddAs(daemon, bobToken, "http://a.test/b.zip");
        var eves = await AddAs(daemon, eveToken, "http://a.test/e.zip");
        var admins = await AddAs(daemon, daemon.Token, "http://a.test/a.zip");

        Assert.Equal(new[] { bobs }, await ListIds(daemon, bobToken));
        Assert.Equal(new[] { eves }, await ListIds(daemon, eveToken));
        Assert.Equal(new[] { bobs, eves, admins }.Order(), (await ListIds(daemon, daemon.Token)).Order());
    }

    [Fact]
    public async Task The_admin_can_filter_by_user_and_a_member_cannot_ask_for_someone_else()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, bobToken) = await daemon.AddUserAsync("bob");
        var (eve, _) = await daemon.AddUserAsync("eve");
        var bobs = await AddAs(daemon, bobToken, "http://a.test/b.zip");

        Assert.Equal(new[] { bobs }, await ListIds(daemon, daemon.Token, $"?user={bob.UserId}"));
        Assert.Empty(await ListIds(daemon, daemon.Token, $"?user={eve.UserId}"));
        Assert.Equal(new[] { bobs }, await ListIds(daemon, bobToken, $"?user={bob.UserId}"));
        var snoop = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/downloads?user={eve.UserId}", token: bobToken));
        Assert.Equal(HttpStatusCode.Forbidden, snoop.StatusCode);
        Assert.Equal("forbidden", await Code(snoop));
    }

    [Fact]
    public async Task Someone_elses_download_is_a_404_for_every_verb_and_nothing_happens()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");
        var (_, eveToken) = await daemon.AddUserAsync("eve");
        var bobs = await AddAs(daemon, bobToken, "http://a.test/b.zip");
        var callsBefore = daemon.Downloads.Calls.Count;

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/v1/downloads/{bobs}"),
            (HttpMethod.Post, $"/api/v1/downloads/{bobs}/pause"),
            (HttpMethod.Post, $"/api/v1/downloads/{bobs}/resume"),
            (HttpMethod.Post, $"/api/v1/downloads/{bobs}/cancel"),
            (HttpMethod.Delete, $"/api/v1/downloads/{bobs}"),
        })
        {
            var response = await daemon.Http.SendAsync(daemon.Request(method, path, token: eveToken));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("download_not_found", await Code(response));
        }

        Assert.Equal(callsBefore, daemon.Downloads.Calls.Count);
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/downloads/{bobs}", token: bobToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, $"/api/v1/downloads/{bobs}/pause"))).StatusCode);
    }

    [Fact]
    public async Task Downloads_from_before_users_existed_belong_to_the_admin()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");
        var legacy = daemon.Downloads.Seed("old.zip", DownloadStatus.Completed); // OwnerId stays null

        Assert.Contains(legacy.Id, await ListIds(daemon, daemon.Token));
        Assert.Empty(await ListIds(daemon, bobToken));
        Assert.Equal(HttpStatusCode.NotFound,
            (await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/downloads/{legacy.Id}", token: bobToken))).StatusCode);
    }

    [Fact]
    public async Task Downloads_record_who_added_them_and_the_response_says_so()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (bob, bobToken) = await daemon.AddUserAsync("bob");

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add("http://a.test/b.zip"), bobToken));
        var item = (await Json(response)).GetProperty("items")[0];

        Assert.Equal(bob.UserId, item.GetProperty("ownerId").GetGuid());
        Assert.Equal(bob.UserId, daemon.Downloads.Tasks.Single().OwnerId);
    }

    [Fact]
    public async Task A_members_downloads_go_to_their_own_folder_unless_told_otherwise_inside_it()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");

        await AddAs(daemon, bobToken, "http://a.test/b.zip");
        var inside = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads",
            Add("http://a.test/c.zip", "/downloads-default/bob/movies"), bobToken));

        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
        Assert.Equal(new[]
        {
            "add http://a.test/b.zip -> /downloads-default/bob",
            "add http://a.test/c.zip -> /downloads-default/bob/movies",
        }, daemon.Downloads.Calls);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/downloads-default")]
    [InlineData("/downloads-default/eve")]
    [InlineData("/downloads-default/bob/../eve")]
    [InlineData("/downloads-default/bob-evil")]
    public async Task A_member_cannot_save_outside_their_folder(string destination)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads",
            Add("http://a.test/b.zip", destination), bobToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("destination_not_allowed", await Code(response));
        Assert.Empty(daemon.Downloads.Calls);
    }

    [Fact]
    public async Task The_admin_may_save_anywhere_and_the_default_stays_the_downloads_folder()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add("http://a.test/a.zip", "/srv/media")));
        await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add("http://a.test/b.zip")));

        Assert.Equal(new[] { "add http://a.test/a.zip -> /srv/media", "add http://a.test/b.zip -> /downloads-default" }, daemon.Downloads.Calls);
    }

    [Fact]
    public async Task One_idempotency_key_used_by_two_users_creates_two_downloads()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var (_, bobToken) = await daemon.AddUserAsync("bob");
        var (_, eveToken) = await daemon.AddUserAsync("eve");

        foreach (var token in new[] { bobToken, eveToken })
        {
            var request = daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add("http://a.test/x.zip"), token);
            request.Headers.Add("Idempotency-Key", "same-key");
            Assert.Equal(HttpStatusCode.Created, (await daemon.Http.SendAsync(request)).StatusCode);
        }

        Assert.Equal(2, daemon.Downloads.Calls.Count);
    }
}
