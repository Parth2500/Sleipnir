using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Sleipnir.Server.Access;

/// <summary>
/// How requests reach Sleipnir and how it learns who sent them (spec §6, §7.1). There are two: the
/// loopback port with the local token (alone), and Bifröst's private socket (an extension). Sleipnir
/// runs exactly one of them.
/// </summary>
public interface IFrontDoor
{
    /// <summary><c>alone</c> or <c>extension</c>, as <c>GET /api/v1/server</c> reports it.</summary>
    string Mode { get; }

    /// <summary>True when the door listens on TCP ports, so a busy port should move on to the next one.</summary>
    bool UsesPorts { get; }

    void Listen(KestrelServerOptions kestrel, int port);

    /// <summary>Checks every request, public ones included. A refusal is returned, never written.</summary>
    IResult? Admit(HttpContext context);

    /// <summary>The caller, or the refusal to send instead.</summary>
    ValueTask<DoorResult> IdentifyAsync(HttpContext context);
}

public sealed record DoorResult(Caller? Caller, IResult? Refusal)
{
    public static DoorResult Allow(Caller caller) => new(caller, null);

    public static DoorResult Refuse(IResult refusal) => new(null, refusal);
}
