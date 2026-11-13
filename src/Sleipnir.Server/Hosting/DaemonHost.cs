using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sleipnir.Core.Interfaces;
using Sleipnir.Server.Identity;
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
    private readonly IdentityService _identity;
    private readonly AuthFailureLimiter _limiter = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private DaemonHost(DaemonOptions options, IDownloadService downloads, IdentityService identity)
    {
        _options = options;
        _downloads = downloads;
        _identity = identity;
    }

    public int Port { get; private set; }

    public static DaemonHost Create(DaemonOptions options, IDownloadService downloads, IdentityService identity) =>
        new(options, downloads, identity);

    public async Task StartAsync(CancellationToken ct = default)
    {
        var first = _options.Port;
        var attempts = first == 0 ? 1 : PortAttempts;
        // Both loopback addresses share one port. IPv6 is used only if this machine can really bind
        // ::1 (the OS can claim support while it is disabled by sysctl).
        var withIPv6 = first != 0 && IPv6LoopbackUsable.Value;
        Exception? last = null;

        for (var i = 0; i < attempts; i++)
        {
            var app = Build(first == 0 ? 0 : first + i, withIPv6);
            try
            {
                await app.StartAsync(ct);
                _app = app;
                Port = new Uri(app.Urls.First()).Port;
                return;
            }
            catch (Exception e) when (IsAddressInUse(e))
            {
                last = e; // only a busy port moves on to the next one
                await app.DisposeAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        throw new IOException($"Could not listen on loopback ports {first}–{first + attempts - 1}: they are all in use.", last);
    }

    private static readonly Lazy<bool> IPv6LoopbackUsable = new(() =>
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    });

    private static bool IsAddressInUse(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException!)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return true;
            if (inner.GetType().Name == "AddressInUseException") return true;
            if (inner.InnerException is null) break;
        }
        return false;
    }

    public Task WaitForShutdownAsync(CancellationToken ct = default) => _shutdown.Task.WaitAsync(ct);

    public async Task StopAsync()
    {
        _shutdown.TrySetResult();
        if (_app is { } app)
        {
            _app = null;
            // Don't let one slow or stuck client hold up saving progress: the container runtime
            // will SIGKILL us a few seconds after SIGTERM.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(grace.Token); }
            catch (OperationCanceledException) { }
            await app.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private WebApplication Build(int port, bool withIPv6)
    {
        // An empty builder reads no appsettings.json and no environment variables, so nothing outside
        // this process can add a listener (for example Kestrel__Endpoints__*) or widen the bind address.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None); // busy-port retries are normal
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.Services.AddRoutingCore();
        builder.WebHost.UseKestrelCore();
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
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();
        app.Use(ReportFailuresAsProblems);
        app.Use(RejectForeignHosts);
        app.Use(Authenticate);

        var api = app.MapGroup("/api/v1");
        ServerEndpoints.Map(api, () => _shutdown.TrySetResult());
        MeEndpoints.Map(api);
        new UserEndpoints(_identity, _downloads).Map(api);
        new DownloadEndpoints(_downloads, _options.ResolvedDestination).Map(api);
        app.MapFallback("/api/{**rest}", () => Problem.Create(404, "not_found", "No such endpoint."));
        return app;
    }

    private static async Task ReportFailuresAsProblems(HttpContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away or the server is stopping: nothing to report.
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            var code = e.StatusCode == 413 ? "payload_too_large" : "invalid_request";
            await Problem.WriteAsync(context, e.StatusCode, code, "The request could not be read.");
        }
        catch (Exception e) when (!context.Response.HasStarted)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<DaemonHost>()
                .LogError(e, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await Problem.WriteAsync(context, 500, "internal_error", "Something went wrong.");
        }
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

        var presented = BearerToken(context.Request);
        if (presented is null)
        {
            // No credentials (what a cross-site browser request looks like): refuse, but don't let
            // anyone use these to exhaust the budget that protects the real owner.
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problem.WriteAsync(context, 401, "unauthorized", "A valid bearer token is required.");
            return;
        }

        // A known, unrevoked device token always works: a 256-bit token can't be guessed, so throttling
        // only needs to slow down wrong guesses, and must never lock the owner out.
        if (await _identity.AuthenticateAsync(presented) is { } principal)
        {
            context.SetPrincipal(principal);
            await next();
            return;
        }

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_limiter.IsBlocked(source))
        {
            await Problem.WriteAsync(context, 429, "too_many_attempts", "Too many failed attempts. Try again later.");
            return;
        }
        _limiter.RecordFailure(source);
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await Problem.WriteAsync(context, 401, "unauthorized", "A valid bearer token is required.");
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }
}
