using System.Net;
using System.Text.Json;
using Sleipnir.Core.Models;
using static Sleipnir.Tests.Server.ExtensionFixture;

namespace Sleipnir.Tests.Server;

public class ExtensionDoorTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Code(HttpResponseMessage response) => (await Json(response)).GetProperty("code").GetString()!;

    private static string Add(params string[] urls) => JsonSerializer.Serialize(new { urls });

    private static IEnumerable<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid());

    [Fact]
    public async Task Server_info_says_the_mode_is_extension()
    {
        await using var ext = await StartAsync();

        var json = await Json(await ext.Http.GetAsync("/api/v1/server"));

        Assert.Equal("extension", json.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_request_without_identity_is_refused()
    {
        await using var ext = await StartAsync();

        var response = await ext.Send(HttpMethod.Get, "/api/v1/downloads", person: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no_identity", await Code(response));
    }

    public static TheoryData<string, string?> Malformations => new()
    {
        { "X-Bifrost-Role", "Admin" },
        { "X-Bifrost-Role", "root" },
        { "X-Bifrost-Role", "admin, member" },
        { "X-Bifrost-Person-Id", "abc" },
        { "X-Bifrost-Device-Kind", "tv" },
        { "X-Bifrost-Device-Id", null },
    };

    [Theory]
    [MemberData(nameof(Malformations))]
    public async Task Malformed_identity_is_refused(string header, string? value)
    {
        await using var ext = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/downloads");
        var headers = new Dictionary<string, string>
        {
            ["X-Bifrost-Person-Id"] = Alice.ToString(), ["X-Bifrost-Role"] = "admin",
            ["X-Bifrost-Device-Id"] = Device.ToString(), ["X-Bifrost-Device-Kind"] = "app",
        };
        if (value is null) headers.Remove(header); else headers[header] = value;
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);

        var response = await ext.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no_identity", await Code(response));
    }

    [Fact]
    public async Task A_repeated_identity_header_is_refused()
    {
        await using var ext = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/downloads");
        request.Headers.Add("X-Bifrost-Person-Id", Alice.ToString());
        request.Headers.Add("X-Bifrost-Role", "member");
        request.Headers.Add("X-Bifrost-Role", "admin");
        request.Headers.Add("X-Bifrost-Device-Id", Device.ToString());
        request.Headers.Add("X-Bifrost-Device-Kind", "app");

        var response = await ext.Http.SendAsync(request);

        Assert.Equal("no_identity", await Code(response));
    }

    [Fact]
    public async Task A_member_sees_only_their_own_downloads_and_others_look_missing()
    {
        await using var ext = await StartAsync();
        var alices = ext.Downloads.Seed("alice.zip", DownloadStatus.Paused);
        alices.OwnerId = Alice;
        var bobs = ext.Downloads.Seed("bob.zip", DownloadStatus.Paused);
        bobs.OwnerId = Bob;

        var list = await Json(await ext.Send(HttpMethod.Get, "/api/v1/downloads", Bob));
        var peek = await ext.Send(HttpMethod.Get, $"/api/v1/downloads/{alices.Id}", Bob);
        var pause = await ext.Send(HttpMethod.Post, $"/api/v1/downloads/{alices.Id}/resume", Bob);
        var snoop = await ext.Send(HttpMethod.Get, $"/api/v1/downloads?person={Alice}", Bob);

        Assert.Equal([bobs.Id], Ids(list));
        Assert.Equal("download_not_found", await Code(peek));
        Assert.Equal("download_not_found", await Code(pause));
        Assert.Equal(HttpStatusCode.Forbidden, snoop.StatusCode);
        Assert.Equal(DownloadStatus.Paused, alices.Status);
    }

    [Fact]
    public async Task An_admin_sees_everyones_downloads_and_can_filter_by_person()
    {
        await using var ext = await StartAsync();
        ext.Downloads.Seed("alice.zip", DownloadStatus.Paused).OwnerId = Alice;
        var bobs = ext.Downloads.Seed("bob.zip", DownloadStatus.Paused);
        bobs.OwnerId = Bob;

        var all = await Json(await ext.Send(HttpMethod.Get, "/api/v1/downloads", Alice, "admin"));
        var filtered = await Json(await ext.Send(HttpMethod.Get, $"/api/v1/downloads?person={Bob}", Alice, "admin"));

        Assert.Equal(2, Ids(all).Count());
        Assert.Equal([bobs.Id], Ids(filtered));
    }

    [Fact]
    public async Task A_browser_fallback_device_is_view_only()
    {
        await using var ext = await StartAsync();
        var mine = ext.Downloads.Seed("mine.zip", DownloadStatus.Completed);
        mine.OwnerId = Bob;
        ext.Downloads.Seed("other.zip", DownloadStatus.Completed).OwnerId = Alice;

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Post, "/api/v1/downloads", Add("http://example.test/a.zip")),
            (HttpMethod.Post, $"/api/v1/downloads/{mine.Id}/pause", null),
            (HttpMethod.Post, $"/api/v1/downloads/{mine.Id}/cancel", null),
            (HttpMethod.Delete, $"/api/v1/downloads/{mine.Id}", null),
            (HttpMethod.Post, "/api/v1/server/shutdown", null),
        })
        {
            var response = await ext.Send(method, path, Bob, kind: "browser-fallback", body: body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("view_only", await Code(response));
        }

        var list = await Json(await ext.Send(HttpMethod.Get, "/api/v1/downloads", Bob, kind: "browser-fallback"));
        Assert.Equal([mine.Id], Ids(list));
        Assert.Empty(ext.Downloads.Calls);
    }

    [Fact]
    public async Task A_member_cannot_shut_Sleipnir_down()
    {
        await using var ext = await StartAsync();

        var response = await ext.Send(HttpMethod.Post, "/api/v1/server/shutdown", Bob);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await Code(response));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("admin")]
    public async Task A_destination_from_the_client_is_refused(string role)
    {
        await using var ext = await StartAsync();
        var body = JsonSerializer.Serialize(new { urls = new[] { "http://example.test/a.zip" }, destination = "/srv/media" });

        var response = await ext.Send(HttpMethod.Post, "/api/v1/downloads", Bob, role, body: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("destination_not_allowed", await Code(response));
        Assert.Empty(ext.Downloads.Calls);
    }

    [Fact]
    public async Task A_download_goes_to_the_persons_interim_folder_and_belongs_to_them()
    {
        await using var ext = await StartAsync();

        var response = await ext.Send(HttpMethod.Post, "/api/v1/downloads", Bob, body: Add("http://example.test/a.zip"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([$"add http://example.test/a.zip -> {Data}/downloads/{Bob}"], ext.Downloads.Calls);
        Assert.Equal(Bob, ext.Downloads.Tasks.Single().OwnerId);
    }

    [Fact]
    public async Task The_socket_is_owner_only()
    {
        await using var ext = await StartAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ext.SocketPath));
    }

    [Fact]
    public async Task A_stale_socket_file_is_replaced()
    {
        await using var ext = await StartAsync(beforeStart: socket => File.WriteAllText(socket, "left over"));

        Assert.Equal(HttpStatusCode.OK, (await ext.Http.GetAsync("/api/v1/server")).StatusCode);
    }

    [Fact]
    public async Task No_Host_check_applies_on_the_socket()
    {
        await using var ext = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/server");
        request.Headers.Host = "sleipnir.invalid";

        Assert.Equal(HttpStatusCode.OK, (await ext.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_members_download_is_public_only_and_an_admins_is_not()
    {
        await using var ext = await StartAsync();

        await ext.Send(HttpMethod.Post, "/api/v1/downloads", Bob, body: Add("http://example.test/member.zip"));
        await ext.Send(HttpMethod.Post, "/api/v1/downloads", Alice, "admin", body: Add("http://example.test/admin.zip"));

        Assert.True(ext.Downloads.Tasks.Single(t => t.Filename == "member.zip").PublicAddressesOnly);
        Assert.False(ext.Downloads.Tasks.Single(t => t.Filename == "admin.zip").PublicAddressesOnly);
    }
}
