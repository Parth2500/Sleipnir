namespace Sleipnir.Services;

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

    /// <summary>
    /// Separate retry budget for "server busy" replies (HTTP 429/503). These wait for the server's
    /// Retry-After, or back off exponentially up to <see cref="MaxBusyDelay"/>.
    /// </summary>
    public int MaxBusyRetries { get; init; } = 10;

    /// <summary>Longest wait between "server busy" retries.</summary>
    public TimeSpan MaxBusyDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Bytes buffered in memory per segment before being written to disk.</summary>
    public int FlushThreshold { get; init; } = 512 * 1024;

    /// <summary>How many downloads may transfer at the same time; the rest wait as Queued.</summary>
    public int MaxConcurrentDownloads { get; init; } = 3;

    /// <summary>A connection that delivers no data for this long is treated as failed and retried.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often progress of running downloads is saved to the state store.</summary>
    public TimeSpan StateSaveInterval { get; init; } = TimeSpan.FromSeconds(2);
}
