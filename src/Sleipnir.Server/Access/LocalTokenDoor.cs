using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sleipnir.Server.Api;
using Sleipnir.Server.Security;

namespace Sleipnir.Server.Access;

/// <summary>
/// Sleipnir alone (spec §6.1): HTTP on 127.0.0.1 and ::1, the <c>Host</c> allowlist against DNS
/// rebinding, and the local token in the <c>Authorization</c> header. The holder of the token is the
/// person at this machine.
/// </summary>
public sealed class LocalTokenDoor(string token) : IFrontDoor
{
    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "[::1]"];
    private readonly AuthFailureLimiter _limiter = new();

    public string Mode => "alone";

    public bool UsesPorts => true;

    public void Listen(KestrelServerOptions kestrel, int port)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        kestrel.Listen(IPAddress.Loopback, port);
        // IPv6 is used only if this machine can really bind ::1 (the OS can claim support while it is
        // disabled by sysctl), and never with port 0, where the two would get different ports.
        if (port != 0 && IPv6LoopbackUsable.Value) kestrel.Listen(IPAddress.IPv6Loopback, port);
    }

    public IResult? Admit(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AllowedHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase)
            ? null
            : Problem.Create(400, "host_not_allowed", "This server only answers to its loopback names.");
    }

    public ValueTask<DoorResult> IdentifyAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var presented = BearerToken(context.Request);
        if (presented is null)
        {
            // No credentials (what a cross-site browser request looks like): refuse, but don't let
            // anyone use these to exhaust the budget that protects the real owner.
            return new(DoorResult.Refuse(Unauthorized(context)));
        }

        // The right token always works: a 256-bit token can't be guessed, so throttling only needs to
        // slow down wrong guesses, and must never lock the owner out.
        if (LocalAdminToken.Matches(token, presented)) return new(DoorResult.Allow(Caller.Alone));

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_limiter.IsBlocked(source))
        {
            return new(DoorResult.Refuse(Problem.Create(429, "too_many_attempts", "Too many failed attempts. Try again later.")));
        }
        _limiter.RecordFailure(source);
        return new(DoorResult.Refuse(Unauthorized(context)));
    }

    private static IResult Unauthorized(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Problem.Create(401, "unauthorized", "A valid bearer token is required.");
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
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
}
