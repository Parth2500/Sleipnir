using System;

namespace DotNetDownloadManager.Core.Models;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long BytesDownloaded { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
    public System.Collections.Generic.List<DownloadSegment> Segments { get; set; } = new();
    
    // Calculated property for progress percentage
    public double Progress => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
}
