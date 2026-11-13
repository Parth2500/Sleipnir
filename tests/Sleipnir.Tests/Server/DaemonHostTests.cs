using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sleipnir.Server.Hosting;
using Sleipnir.Server.Security;

namespace Sleipnir.Tests.Server;

public class DaemonHostTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Server_info_is_public_and_says_nothing_else()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.GetAsync("/api/v1/server");
        var json = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sleipnir", json.GetProperty("name").GetString());
        Assert.Equal(new[] { "v1" }, json.GetProperty("apiVersions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "apiVersions", "name", "version" }, json.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Shutdown_without_a_token_is_rejected_as_problem_json()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.PostAsync("/api/v1/server/shutdown", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthorized", (await Json(response)).GetProperty("code").GetString());
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Wrong_tokens_of_any_shape_are_rejected()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var sameLengthWrong = daemon.Token[..^1] + (daemon.Token[^1] == 'a' ? 'b' : 'a');

        foreach (var token in new[] { "wrong", sameLengthWrong, daemon.Token + "x" })
        {
            var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/server/shutdown", token: token));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var basic = new HttpRequestMessage(HttpMethod.Post, "/api/v1/server/shutdown");
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic " + daemon.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(basic)).StatusCode);
    }

    [Fact]
    public async Task A_token_in_the_query_string_or_a_cookie_is_not_accepted()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var query = await daemon.Http.PostAsync($"/api/v1/server/shutdown?access_token={daemon.Token}", null);
        var cookie = new HttpRequestMessage(HttpMethod.Post, "/api/v1/server/shutdown");
        cookie.Headers.Add("Cookie", $"token={daemon.Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.Http.SendAsync(cookie)).StatusCode);
    }

    [Fact]
    public async Task Shutdown_with_the_token_is_accepted_and_signals_the_host()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Post, "/api/v1/server/shutdown"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await daemon.Host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:7766")]
    [InlineData("127.0.0.1.evil.example")]
    public async Task Requests_with_a_foreign_Host_header_are_rejected_even_with_a_valid_token(string host)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var request = daemon.Request(HttpMethod.Get, "/api/v1/server");
        request.Headers.Host = host;

        var response = await daemon.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("host_not_allowed", (await Json(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("localhost:1234")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Loopback_Host_headers_are_accepted(string host)
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/server");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task No_CORS_headers_are_ever_sent()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        var simple = new HttpRequestMessage(HttpMethod.Get, "/api/v1/server");
        simple.Headers.Add("Origin", "http://evil.example");
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/downloads");
        preflight.Headers.Add("Origin", "http://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        foreach (var request in new[] { simple, preflight })
        {
            var response = await daemon.Http.SendAsync(request);
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task After_twenty_failed_attempts_even_the_right_token_is_refused_for_a_while()
    {
        await using var daemon = await DaemonFixture.StartAsync();
        for (var i = 0; i < 20; i++)
        {
            await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me", token: "wrong"));
        }

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/me"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("too_many_attempts", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public void The_failure_limiter_forgets_old_failures()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limiter = new AuthFailureLimiter(maxFailures: 3, window: TimeSpan.FromMinutes(1), clock: () => now);
        for (var i = 0; i < 3; i++) limiter.RecordFailure("a");

        Assert.True(limiter.IsBlocked("a"));
        Assert.False(limiter.IsBlocked("b"));

        now = now.AddMinutes(2);
        Assert.False(limiter.IsBlocked("a"));
    }

    [Fact]
    public async Task Unknown_api_paths_are_404_problem_json()
    {
        await using var daemon = await DaemonFixture.StartAsync();

        var response = await daemon.Http.SendAsync(daemon.Request(HttpMethod.Get, "/api/v1/nope"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_busy_port_makes_the_host_take_the_next_free_one()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var daemon = await DaemonFixture.StartAsync(port: busy);

        Assert.NotEqual(busy, daemon.Port);
        Assert.InRange(daemon.Port, busy + 1, busy + 20);
        Assert.Equal(HttpStatusCode.OK, (await daemon.Http.GetAsync("/api/v1/server")).StatusCode);
    }
}
