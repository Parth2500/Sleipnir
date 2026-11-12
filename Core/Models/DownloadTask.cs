namespace DotNetDownloadManager.Core.Models;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;

    /// <summary>Final path of the file. The in-progress data lives at <c>DestinationPath + ".dnm"</c>.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    public string Filename { get; set; } = string.Empty;
    public long TotalBytes { get; set; }

    /// <summary>Whether the server honours Range requests (segmented download and resume).</summary>
    public bool SupportsRanges { get; set; }

    /// <summary>False when the server did not report a size; <see cref="TotalBytes"/> is then 0 until the download finishes.</summary>
    public bool IsSizeKnown { get; set; } = true;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Segments of this download. The list is replaced as a whole (never mutated in place)
    /// so the UI thread can enumerate it safely while a download runs.
    /// </summary>
    public IReadOnlyList<DownloadSegment> Segments { get; set; } = Array.Empty<DownloadSegment>();

    /// <summary>Sum of bytes received across all segments.</summary>
    public long BytesDownloaded
    {
        get
        {
            long total = 0;
            foreach (var segment in Segments) total += segment.BytesDownloaded;
            return total;
        }
    }

    public string TempFilePath => DestinationPath + ".dnm";

    public double Progress => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
}
