using Sleipnir.Server.Access;
using Sleipnir.Server.Hosting;
using Sleipnir.Tests.Tui;

namespace Sleipnir.Tests.Server;

/// <summary>A real daemon alone, on an ephemeral loopback port, with the local token and an in-memory download service.</summary>
public sealed class DaemonFixture : IAsyncDisposable
{
    public required DaemonHost Host { get; init; }
    public required HttpClient Http { get; init; }
    public required string Token { get; init; }
    public required StubDownloadService Downloads { get; init; }
    public int Port => Host.Port;

    public static async Task<DaemonFixture> StartAsync(StubDownloadService? downloads = null, int port = 0)
    {
        downloads ??= new StubDownloadService();
        var token = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..43];

        var host = DaemonHost.Create(new DaemonOptions(port, "/downloads-default"), downloads, new LocalTokenDoor(token));
        await host.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        return new DaemonFixture { Host = host, Http = http, Token = token, Downloads = downloads };
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? Token);
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
    }
}
