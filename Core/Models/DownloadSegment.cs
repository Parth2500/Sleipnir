namespace DotNetDownloadManager.Core.Models;

/// <summary>
/// A contiguous byte range of a download, fetched by its own HTTP request.
/// </summary>
public class DownloadSegment
{
    private long _bytesDownloaded;

    public int Index { get; set; }

    /// <summary>First byte of the range (inclusive).</summary>
    public long StartOffset { get; set; }

    /// <summary>Last byte of the range (inclusive).</summary>
    public long EndOffset { get; set; }

    /// <summary>
    /// Bytes received for this segment. Written by the download thread and read by the UI,
    /// so access is atomic. After a pause/failure this equals the bytes safely on disk.
    /// </summary>
    public long BytesDownloaded
    {
        get => Interlocked.Read(ref _bytesDownloaded);
        set => Interlocked.Exchange(ref _bytesDownloaded, value);
    }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public long TotalBytes => EndOffset - StartOffset + 1;
}
