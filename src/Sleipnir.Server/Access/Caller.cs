using Sleipnir.Core.Models;

namespace Sleipnir.Server.Access;

/// <summary>
/// Who is calling and what they may do (spec §7.1). Alone there is one caller, the person at this
/// machine, who may do everything. Under Bifröst the caller is a Bifröst person on one device.
/// </summary>
public sealed record Caller(Guid? PersonId, bool IsAdmin, bool ViewOnly)
{
    /// <summary>The person at this machine, alone. Downloads made alone have no owner.</summary>
    public static Caller Alone { get; } = new(null, true, false);

    public bool CanSee(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return IsAdmin || task.OwnerId == PersonId;
    }
}

internal static class HttpContextCaller
{
    private const string Key = "sleipnir.caller";

    public static void SetCaller(this HttpContext context, Caller caller) => context.Items[Key] = caller;

    /// <summary>The identified caller. Only valid on routes behind the front door.</summary>
    public static Caller Caller(this HttpContext context) => (Caller)context.Items[Key]!;
}
