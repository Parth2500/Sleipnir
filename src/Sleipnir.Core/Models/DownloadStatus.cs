namespace Sleipnir.Core.Models;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,

    /// <summary>Waiting for an admin's approval (spec §7.4). Last, so stored numbers keep their meaning.</summary>
    Pending
}

/// <summary>Where the approval of a <see cref="DownloadStatus.Pending"/> download stands, or how it ended.</summary>
public enum ApprovalState
{
    /// <summary>The admin has been asked.</summary>
    Waiting,

    /// <summary>Nobody answered in time. The download stays pending; an admin can still approve it.</summary>
    Expired,

    /// <summary>An admin said no. The download is cancelled.</summary>
    Rejected
}
