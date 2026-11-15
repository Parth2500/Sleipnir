using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;
using Sleipnir.Server.Api;

namespace Sleipnir.Server.Access;

/// <summary>
/// Sleipnir as a Bifröst extension (spec §6.2, Bifröst design §8.5): it listens only on the private
/// Unix socket Bifröst gave it, and learns who is calling from the headers Bifröst adds. The headers
/// are trusted because nothing but Bifröst's account can reach a 0600 socket in a 0700 folder.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BifrostDoor(string socketPath) : IFrontDoor
{
    public const string PersonHeader = "X-Bifrost-Person-Id";
    public const string RoleHeader = "X-Bifrost-Role";
    public const string DeviceHeader = "X-Bifrost-Device-Id";
    public const string KindHeader = "X-Bifrost-Device-Kind";

    public string Mode => "extension";

    public bool UsesPorts => false;

    public void Listen(KestrelServerOptions kestrel, int port)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        // The identity headers are only trustworthy if no other account can reach the socket, even in
        // the moment between bind and chmod. Bifröst creates the folder 0700; anything wider is refused.
        var folder = Path.GetDirectoryName(Path.GetFullPath(socketPath))!;
        var mode = File.GetUnixFileMode(folder);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException($"The socket folder {folder} is open to other accounts (mode {Convert.ToString((int)mode, 8)}); it must be 700.");
        }
        // A socket file left by a crash would make the bind fail.
        if (File.Exists(socketPath) || new FileInfo(socketPath).LinkTarget is not null) File.Delete(socketPath);
        kestrel.ListenUnixSocket(socketPath);
    }

    public void OnListening() => File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    /// <summary>No <c>Host</c> check: there is no TCP listener to rebind to.</summary>
    public IResult? Admit(HttpContext context) => null;

    public ValueTask<DoorResult> IdentifyAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Request.Headers;
        if (Single(headers[PersonHeader]) is { } person && Guid.TryParse(person, out var personId)
            && Single(headers[DeviceHeader]) is { } device && Guid.TryParse(device, out _)
            && Single(headers[RoleHeader]) is "admin" or "member"
            && Single(headers[KindHeader]) is "app" or "browser-fallback")
        {
            var caller = new Caller(personId, headers[RoleHeader] == "admin", headers[KindHeader] == "browser-fallback");
            return new(DoorResult.Allow(caller));
        }
        // Never a default: a request Bifröst did not identify is refused (Review Focus 1).
        return new(DoorResult.Refuse(Problem.Create(401, "no_identity", "Bifröst did not say who is calling.")));
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;
}
