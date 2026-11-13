using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sleipnir.Core.Interfaces;
using Sleipnir.Server.Api;
using Sleipnir.Server.Security;

namespace Sleipnir.Server.Hosting;

/// <summary>The local REST API around a download service: loopback only, bearer-token protected.</summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private const int PortAttempts = 20;
    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly DaemonOptions _options;
    private readonly IDownloadService _downloads;
    private readonly AuthFailureLimiter _limiter = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private DaemonHost(DaemonOptions options, IDownloadService downloads)
    {
        _options = options;
        _downloads = downloads;
    }

    public int Port { get; private set; }

    public static DaemonHost Create(DaemonOptions options, IDownloadService downloads) => new(options, downloads);

    public async Task StartAsync(CancellationToken ct = default)
    {
        var first = _options.Port;
        var attempts = first == 0 ? 1 : PortAttempts;
        Exception? last = null;

        for (var i = 0; i < attempts; i++)
        {
            var port = first == 0 ? 0 : first + i;
            // IPv6 loopback is best effort: a machine without it still gets the IPv4 listener.
            foreach (var withIPv6 in first == 0 ? [false] : new[] { true, false })
            {
                var app = Build(port, withIPv6);
                try
                {
                    await app.StartAsync(ct);
                    _app = app;
                    Port = new Uri(app.Urls.First()).Port;
                    return;
                }
                catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
                {
                    last = e;
                    await app.DisposeAsync();
                }
            }
        }

        throw new IOException($"Could not listen on loopback ports {first}–{first + attempts - 1}.", last);
    }

    public Task WaitForShutdownAsync(CancellationToken ct = default) => _shutdown.Task.WaitAsync(ct);

    public async Task StopAsync()
    {
        _shutdown.TrySetResult();
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private WebApplication Build(int port, bool withIPv6)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Listen(IPAddress.Loopback, port);
            if (withIPv6) kestrel.Listen(IPAddress.IPv6Loopback, port);
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.Use(RejectForeignHosts);
        app.Use(Authenticate);

        var api = app.MapGroup("/api/v1");
        ServerEndpoints.Map(api, () => _shutdown.TrySetResult());
        app.MapFallback("/api/{**rest}", () => Problem.Create(404, "not_found", "No such endpoint."));
        return app;
    }

    private static async Task RejectForeignHosts(HttpContext context, Func<Task> next)
    {
        var host = context.Request.Host.Host;
        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            await Problem.WriteAsync(context, 400, "host_not_allowed", "This server only answers to its loopback names.");
            return;
        }
        await next();
    }

    private async Task Authenticate(HttpContext context, Func<Task> next)
    {
        var isPublic = HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/api/v1/server";
        if (isPublic || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_limiter.IsBlocked(source))
        {
            await Problem.WriteAsync(context, 429, "too_many_attempts", "Too many failed attempts. Try again later.");
            return;
        }

        if (!LocalAdminToken.Matches(_options.AdminToken, BearerToken(context.Request)))
        {
            _limiter.RecordFailure(source);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problem.WriteAsync(context, 401, "unauthorized", "A valid bearer token is required.");
            return;
        }
        await next();
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }
}
