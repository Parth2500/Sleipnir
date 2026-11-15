using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sleipnir.Core.Interfaces;
using Sleipnir.Server.Access;
using Sleipnir.Server.Api;

namespace Sleipnir.Server.Hosting;

/// <summary>The REST API around a download service, behind one front door (spec §6): alone or under Bifröst.</summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private const int PortAttempts = 20;
    private readonly DaemonOptions _options;
    private readonly IDownloadService _downloads;
    private readonly IFrontDoor _door;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private DaemonHost(DaemonOptions options, IDownloadService downloads, IFrontDoor door)
    {
        _options = options;
        _downloads = downloads;
        _door = door;
    }

    public int Port { get; private set; }

    public static DaemonHost Create(DaemonOptions options, IDownloadService downloads, IFrontDoor door) =>
        new(options, downloads, door);

    public async Task StartAsync(CancellationToken ct = default)
    {
        var first = _options.Port;
        var attempts = !_door.UsesPorts || first == 0 ? 1 : PortAttempts;
        Exception? last = null;

        for (var i = 0; i < attempts; i++)
        {
            var app = Build(first == 0 ? 0 : first + i);
            try
            {
                await app.StartAsync(ct);
                _app = app;
                if (_door.UsesPorts) Port = new Uri(app.Urls.First()).Port;
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

    private WebApplication Build(int port)
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
            _door.Listen(kestrel, port);
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();
        app.Use(ReportFailuresAsProblems);
        app.Use(Admit);
        app.Use(Identify);

        var api = app.MapGroup("/api/v1");
        ServerEndpoints.Map(api, _door.Mode, () => _shutdown.TrySetResult());
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

    private async Task Admit(HttpContext context, Func<Task> next)
    {
        if (_door.Admit(context) is { } refusal)
        {
            await refusal.ExecuteAsync(context);
            return;
        }
        await next();
    }

    private async Task Identify(HttpContext context, Func<Task> next)
    {
        var isPublic = HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/api/v1/server";
        if (isPublic || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }

        var result = await _door.IdentifyAsync(context);
        if (result.Caller is not { } caller)
        {
            await (result.Refusal ?? Problem.Create(401, "unauthorized", "Who is calling is not known.")).ExecuteAsync(context);
            return;
        }
        context.SetCaller(caller);
        await next();
    }
}
