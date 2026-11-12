namespace DotNetDownloadManager.Services;

/// <summary>
/// Tunables for <see cref="DownloadService"/>.
/// </summary>
public sealed class DownloadOptions
{
    /// <summary>Maximum number of parallel segments per download.</summary>
    public int SegmentCount { get; init; } = 8;

    /// <summary>Segments are never smaller than this (small files use fewer segments).</summary>
    public long MinSegmentSize { get; init; } = 1024 * 1024;

    /// <summary>How many times a failed segment is retried before the download fails.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base delay between retries; attempt N waits N × this value.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Bytes buffered in memory per segment before being written to disk.</summary>
    public int FlushThreshold { get; init; } = 512 * 1024;
}
