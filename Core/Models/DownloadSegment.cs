using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Core.Models;

public class DownloadSegment
{
    public int Index { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public long BytesDownloaded { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalBytes => EndOffset - StartOffset + 1;
}
