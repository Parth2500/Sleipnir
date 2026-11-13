using Sleipnir.Core.Models;

namespace Sleipnir.Core.Api;

public sealed record SegmentDto(int Index, long StartOffset, long EndOffset, long BytesDownloaded);

/// <param name="Segments">Only present when a single download is fetched.</param>
public sealed record DownloadDto(
    Guid Id,
    string Url,
    string Filename,
    string DestinationPath,
    DownloadStatus Status,
    long TotalBytes,
    bool IsSizeKnown,
    long BytesDownloaded,
    string? ErrorMessage,
    DateTime CreatedAt,
    SegmentDto[]? Segments)
{
    public static DownloadDto From(DownloadTask task, bool includeSegments = false) => new(
        task.Id, task.Url, task.Filename, task.DestinationPath, task.Status, task.TotalBytes, task.IsSizeKnown,
        task.BytesDownloaded, task.ErrorMessage, task.CreatedAt,
        includeSegments
            ? task.Segments.Select(s => new SegmentDto(s.Index, s.StartOffset, s.EndOffset, s.BytesDownloaded)).ToArray()
            : null);
}

/// <param name="Destination">Folder to save into; the server's default folder when omitted.</param>
public sealed record AddDownloadsRequest(string[]? Urls, string? Destination);

public sealed record PageDto<T>(T[] Items, string? NextCursor);
