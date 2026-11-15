using Sleipnir.Core.Models;
using Sleipnir.Server.Hosting;
using Sleipnir.Server.Identity;
using Sleipnir.Server.Persistence;
using Sleipnir.Tests.Tui;

namespace Sleipnir.Tests.Server;

/// <summary>A real daemon on an ephemeral loopback port, with a real identity store and an in-memory download service.</summary>
public sealed class DaemonFixture : IAsyncDisposable
{
    public required DaemonHost Host { get; init; }
    public required HttpClient Http { get; init; }
    public required string Token { get; init; }
    public required StubDownloadService Downloads { get; init; }
    public required IdentityService Identity { get; init; }
    public required SleipnirDbFactory Database { get; init; }
    public required Principal Admin { get; init; }
    public int Port => Host.Port;

    public static async Task<DaemonFixture> StartAsync(StubDownloadService? downloads = null, int port = 0)
    {
        downloads ??= new StubDownloadService();
        var database = Sleipnir.Server.Persistence.Database.OpenMemory();
        await Sleipnir.Server.Persistence.Database.MigrateAsync(database);
        var identity = new IdentityService(database);
        var token = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..43];
        var admin = await identity.EnsureLocalAdminAsync("admin", token);

        var host = DaemonHost.Create(new DaemonOptions(port, "/downloads-default"), downloads, identity);
        await host.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };
        return new DaemonFixture
        {
            Host = host, Http = http, Token = token, Downloads = downloads, Identity = identity, Admin = admin,
            Database = database,
        };
    }

    /// <summary>Creates a user with one device and returns who they are and their token.</summary>
    public async Task<(Principal Principal, string Token)> AddUserAsync(string name, UserRole role = UserRole.Member)
    {
        var user = await Identity.CreateUserAsync(name, role);
        var issued = await Identity.IssueDeviceAsync(user.Id, name + "'s phone", "android");
        var principal = (await Identity.AuthenticateAsync(issued.Token))!;
        return (principal, issued.Token);
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
        Database.Dispose();
    }
}
