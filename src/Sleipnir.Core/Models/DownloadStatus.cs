namespace DotNetDownloadManager.Core.Models;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}
