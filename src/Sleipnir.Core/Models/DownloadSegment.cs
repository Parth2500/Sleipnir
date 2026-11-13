namespace Sleipnir.Core.Models;

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

    private long _persistedBytes;

    /// <summary>Bytes of this segment confirmed written to disk. Only this value is saved for resume.</summary>
    public long PersistedBytes
    {
        get => Interlocked.Read(ref _persistedBytes);
        set => Interlocked.Exchange(ref _persistedBytes, value);
    }

    /// <summary>True when the file size is unknown: the segment runs until the server closes the stream.</summary>
    public bool IsOpenEnded { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public long TotalBytes => EndOffset - StartOffset + 1;
}
