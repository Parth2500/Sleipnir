using System.Net;
using System.Text;
using System.Text.Json;
using Sleipnir.Core.Models;

namespace Sleipnir.Tests.Server;

public class DownloadEndpointTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("code").GetString()!;

    private static string Add(params string[] urls) =>
        JsonSerializer.Serialize(new { urls });

    [Fact]
    public async Task An_empty_list_is_an_empty_page()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var json = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/downloads")));

        Assert.Equal(0, json.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Pages_are_walked_with_the_cursor()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var a = daemon.Downloads.Seed("a.zip", DownloadStatus.Queued);
        var b = daemon.Downloads.Seed("b.zip", DownloadStatus.Queued);
        var c = daemon.Downloads.Seed("c.zip", DownloadStatus.Queued);

        var first = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/downloads?limit=2")));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await Json(await daemon.Http.SendAsync(
            daemon.Request(HttpMethod.Get, $"/api/v1/downloads?limit=2&cursor={Uri.EscapeDataString(cursor!)}")));

        Assert.Equal(new[] { a.Id, b.Id }, first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { c.Id }, second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("limit=0", 1)]
    [InlineData("limit=-5", 1)]
    [InlineData("limit=1000", 3)]
    public async Task Limits_are_clamped_to_1_and_200(string query, int expectedCount)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        for (var i = 0; i < 3; i++) daemon.Downloads.Seed($"f{i}.zip", DownloadStatus.Queued);

        var json = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/downloads?" + query)));

        Assert.Equal(expectedCount, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_garbage_cursor_is_a_400()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/downloads?cursor=%21%21garbage"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cursor", await Code(response));
    }

    [Fact]
    public async Task Adding_urls_returns_201_and_uses_the_default_destination()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads",
            Add("http://example.test/a.zip", "https://example.test/b.zip")));
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal("a.zip", json.GetProperty("items")[0].GetProperty("filename").GetString());
        Assert.Equal("queued", json.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(new[]
        {
            "add http://example.test/a.zip -> /downloads-default",
            "add https://example.test/b.zip -> /downloads-default",
        }, daemon.Downloads.Calls);
    }

    [Fact]
    public async Task A_destination_in_the_request_is_passed_through()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var body = JsonSerializer.Serialize(new { urls = new[] { "http://example.test/a.zip" }, destination = "/srv/media" });

        await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", body));

        Assert.Equal(new[] { "add http://example.test/a.zip -> /srv/media" }, daemon.Downloads.Calls);
    }

    [Theory]
    [InlineData("""{"urls":["http://a.test/x","ftp://a.test/y"]}""")]
    [InlineData("""{"urls":["file:///etc/passwd"]}""")]
    [InlineData("""{"urls":["not a url"]}""")]
    [InlineData("""{"urls":[]}""")]
    [InlineData("""{"urls":[""]}""")]
    public async Task Invalid_urls_are_rejected_and_nothing_is_added(string body)
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_url", await Code(response));
        Assert.Empty(daemon.Downloads.Calls);
    }

    [Fact]
    public async Task More_than_100_urls_are_rejected()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var urls = Enumerable.Range(0, 101).Select(i => $"http://a.test/{i}").ToArray();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", Add(urls)));

        Assert.Equal("invalid_url", await Code(response));
        Assert.Empty(daemon.Downloads.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    public async Task Malformed_bodies_are_a_400(string body)
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await Code(response));
    }

    [Fact]
    public async Task A_2_MB_body_is_refused()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var body = "{\"urls\":[\"http://a.test/" + new string('a', 2 * 1024 * 1024) + "\"]}";

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/downloads", body));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(daemon.Downloads.Calls);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_adds_the_download_once()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        HttpRequestMessage Post(string body)
        {
            var request = daemon.Request(HttpMethod.Post, "/api/v1/downloads", body);
            request.Headers.Add("Idempotency-Key", "phone-1");
            return request;
        }

        var first = await Json(await daemon.Http.SendAsync(Post(Add("http://a.test/x.zip"))));
        var second = await Json(await daemon.Http.SendAsync(Post(Add("http://a.test/x.zip"))));

        Assert.Single(daemon.Downloads.Calls);
        Assert.Equal(first.GetProperty("items")[0].GetProperty("id").GetGuid(), second.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_a_422()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        HttpRequestMessage Post(string body)
        {
            var request = daemon.Request(HttpMethod.Post, "/api/v1/downloads", body);
            request.Headers.Add("Idempotency-Key", "phone-1");
            return request;
        }
        await daemon.Http.SendAsync(Post(Add("http://a.test/x.zip")));

        var response = await daemon.Http.SendAsync(Post(Add("http://a.test/other.zip")));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("idempotency_key_reused", await Code(response));
        Assert.Single(daemon.Downloads.Calls);
    }

    [Fact]
    public async Task One_download_includes_its_segments()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var task = daemon.Downloads.Seed("a.zip", DownloadStatus.Downloading, total: 1000, done: 250);

        var json = await Json(await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, $"/api/v1/downloads/{task.Id}")));

        Assert.Equal("downloading", json.GetProperty("status").GetString());
        Assert.Equal(250, json.GetProperty("bytesDownloaded").GetInt64());
        var segment = json.GetProperty("segments")[0];
        Assert.Equal(0, segment.GetProperty("startOffset").GetInt64());
        Assert.Equal(999, segment.GetProperty("endOffset").GetInt64());
        Assert.Equal(250, segment.GetProperty("bytesDownloaded").GetInt64());
    }

    [Fact]
    public async Task Unknown_downloads_are_404_for_every_verb()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var id = Guid.NewGuid();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/v1/downloads/{id}"),
            (HttpMethod.Post, $"/api/v1/downloads/{id}/pause"),
            (HttpMethod.Post, $"/api/v1/downloads/{id}/resume"),
            (HttpMethod.Post, $"/api/v1/downloads/{id}/cancel"),
            (HttpMethod.Delete, $"/api/v1/downloads/{id}"),
        })
        {
            var response = await daemon.Http.SendAsync(daemon.Request(method, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("download_not_found", await Code(response));
        }
    }

    [Theory]
    [InlineData("pause", DownloadStatus.Downloading, "paused")]
    [InlineData("resume", DownloadStatus.Paused, "downloading")]
    [InlineData("cancel", DownloadStatus.Downloading, "cancelled")]
    public async Task Control_endpoints_act_and_return_the_new_state(string verb, DownloadStatus start, string expected)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var task = daemon.Downloads.Seed("a.zip", start);

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, $"/api/v1/downloads/{task.Id}/{verb}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, (await Json(response)).GetProperty("status").GetString());
        Assert.Equal(new[] { $"{verb} a.zip" }, daemon.Downloads.Calls);
    }

    [Fact]
    public async Task Deleting_a_finished_download_removes_it_but_a_running_one_is_a_409()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var done = daemon.Downloads.Seed("done.zip", DownloadStatus.Completed);
        var running = daemon.Downloads.Seed("busy.zip", DownloadStatus.Downloading);

        var removed = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/downloads/{done.Id}"));
        var refused = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Delete, $"/api/v1/downloads/{running.Id}"));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("download_not_finished", await Code(refused));
        Assert.Equal(new[] { running.Id }, daemon.Downloads.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task Every_download_endpoint_needs_a_token()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var task = daemon.Downloads.Seed("a.zip", DownloadStatus.Queued);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/v1/downloads"),
            (HttpMethod.Post, "/api/v1/downloads"),
            (HttpMethod.Get, $"/api/v1/downloads/{task.Id}"),
            (HttpMethod.Post, $"/api/v1/downloads/{task.Id}/pause"),
            (HttpMethod.Delete, $"/api/v1/downloads/{task.Id}"),
        })
        {
            var response = await daemon.Http.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
