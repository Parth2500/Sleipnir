namespace Sleipnir.Core.Models;

/// <summary>
/// Saved state of a download, used to resume it after the application restarts.
/// Segment byte counts are the bytes known to be on disk (never bytes still in memory).
/// </summary>
public sealed record DownloadRecord(
    Guid Id,
    string Url,
    string DestinationPath,
    string Filename,
    long TotalBytes,
    bool IsSizeKnown,
    bool SupportsRanges,
    DownloadStatus Status,
    DateTime CreatedAt,
    string? ErrorMessage,
    IReadOnlyList<SegmentRecord> Segments,
    Guid? OwnerId = null);

public sealed record SegmentRecord(int Index, long StartOffset, long EndOffset, long BytesDownloaded, bool IsOpenEnded);
