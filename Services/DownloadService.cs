using System.Collections.Concurrent;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Services;

/// <summary>
/// Orchestrates downloads: probes the server, splits the file into segments, runs them in
/// parallel with retries, and handles pause / resume / cancel.
/// </summary>
public class DownloadService : IDownloadService
{
    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private readonly DownloadOptions _options;
    private readonly ConcurrentDictionary<Guid, DownloadTask> _downloads = new();
    private readonly ConcurrentDictionary<Guid, DownloadRun> _runs = new();
    private readonly object _gate = new();

    public DownloadService(INetworkClient networkClient, IFileStorage fileStorage, DownloadOptions? options = null)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
        _options = options ?? new DownloadOptions();
    }

    /// <summary>One execution of a download, from start/resume until it finishes, pauses, or is cancelled.</summary>
    private sealed class DownloadRun
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>Why the run was stopped; decides the final status when cancellation is observed.</summary>
        public volatile bool CancelRequested;
    }

    public Task<Guid> AddDownloadAsync(string url, string outputDirectory)
    {
        var uri = new Uri(url);
        var fileName = FileNameResolver.FromUrl(uri);

        DownloadTask task;
        lock (_gate)
        {
            var destination = FileNameResolver.MakeUnique(outputDirectory, fileName, IsPathTaken);
            task = new DownloadTask
            {
                Url = url,
                DestinationPath = destination,
                Filename = Path.GetFileName(destination),
                Status = DownloadStatus.Queued,
            };
            _downloads[task.Id] = task;
            StartRun(task);
        }

        return Task.FromResult(task.Id);
    }

    public async Task PauseDownloadAsync(Guid downloadId)
    {
        if (!_downloads.ContainsKey(downloadId)) return;
        await StopRunAsync(downloadId, cancel: false);
    }

    public async Task ResumeDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;

        // Make sure the previous run has fully exited before starting a new one, so two runs never
        // write the same file.
        if (_runs.TryGetValue(downloadId, out var previous))
        {
            await previous.Completion;
        }

        lock (_gate)
        {
            if (task.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
            if (_runs.ContainsKey(downloadId)) return;

            task.Status = DownloadStatus.Queued;
            task.ErrorMessage = null;
            StartRun(task);
        }
    }

    public async Task CancelDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;
        if (task.Status == DownloadStatus.Completed) return;

        await StopRunAsync(downloadId, cancel: true);

        task.Status = DownloadStatus.Cancelled;
        foreach (var segment in task.Segments)
        {
            if (segment.Status != DownloadStatus.Completed)
            {
                segment.Status = DownloadStatus.Cancelled;
            }
        }

        await _fileStorage.DeleteAsync(task.TempFilePath);
    }

    public IEnumerable<DownloadTask> GetAllDownloads() => _downloads.Values;

    public DownloadTask? GetDownload(Guid downloadId) => _downloads.TryGetValue(downloadId, out var task) ? task : null;

    // Must be called while holding _gate.
    private void StartRun(DownloadTask task)
    {
        var run = new DownloadRun();
        _runs[task.Id] = run;
        run.Completion = Task.Run(() => RunAsync(task, run));
    }

    private async Task StopRunAsync(Guid downloadId, bool cancel)
    {
        DownloadRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(downloadId, out run)) return;
            if (cancel) run.CancelRequested = true;
            run.Cts.Cancel();
        }

        await run.Completion;
    }

    private bool IsPathTaken(string path) =>
        _fileStorage.Exists(path) ||
        _fileStorage.Exists(path + ".dnm") ||
        _downloads.Values.Any(d => string.Equals(d.DestinationPath, path, StringComparison.Ordinal));

    private async Task RunAsync(DownloadTask task, DownloadRun run)
    {
        var token = run.Cts.Token;
        try
        {
            task.Status = DownloadStatus.Downloading;

            // 1. Probe the server once per download.
            if (task.Segments.Count == 0)
            {
                var info = await _networkClient.GetFileInfoAsync(task.Url, token);
                if (info.Size is not long size)
                {
                    throw new InvalidOperationException("The server did not report the file size.");
                }
                task.TotalBytes = size;
                task.SupportsRanges = info.SupportsRanges;
            }

            // 2. (Re)create the temp file if it is missing; any recorded progress is then invalid.
            if (!_fileStorage.Exists(task.TempFilePath))
            {
                await _fileStorage.CreateFileAsync(task.TempFilePath, task.TotalBytes);
                task.Segments = Array.Empty<DownloadSegment>();
            }

            // 3. Download; if the server turns out to ignore ranges, restart as a single stream.
            try
            {
                await DownloadSegmentsAsync(task, token);
            }
            catch (RangeNotSupportedException) when (task.SupportsRanges)
            {
                task.SupportsRanges = false;
                task.Segments = Array.Empty<DownloadSegment>();
                await DownloadSegmentsAsync(task, token);
            }

            // 4. Move the finished temp file into place without overwriting anything.
            string destination;
            lock (_gate)
            {
                destination = _fileStorage.Exists(task.DestinationPath)
                    ? FileNameResolver.MakeUnique(Path.GetDirectoryName(task.DestinationPath)!, task.Filename, IsPathTaken)
                    : task.DestinationPath;
            }
            var tempPath = task.TempFilePath;
            _fileStorage.Move(tempPath, destination);
            task.DestinationPath = destination;
            task.Filename = Path.GetFileName(destination);
            task.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            task.Status = run.CancelRequested ? DownloadStatus.Cancelled : DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
        finally
        {
            lock (_gate)
            {
                // Only remove our own entry; a newer run may already be registered.
                _runs.TryRemove(new KeyValuePair<Guid, DownloadRun>(task.Id, run));
            }
            run.Cts.Dispose();
        }
    }

    private async Task DownloadSegmentsAsync(DownloadTask task, CancellationToken token)
    {
        if (task.Segments.Count == 0)
        {
            task.Segments = CreateSegments(task.TotalBytes, task.SupportsRanges ? _options.SegmentCount : 1);
        }

        if (!task.SupportsRanges)
        {
            // Without ranges a single stream must start from byte 0 every time.
            task.Segments[0].BytesDownloaded = 0;
        }

        // A permanent failure in one segment stops all the others.
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(token);
        var segmentTasks = task.Segments
            .Where(s => s.Status != DownloadStatus.Completed)
            .Select(s => DownloadSegmentWithRetryAsync(task, s, failure))
            .ToList();

        await Task.WhenAll(segmentTasks);
    }

    private async Task DownloadSegmentWithRetryAsync(DownloadTask task, DownloadSegment segment, CancellationTokenSource failure)
    {
        var token = failure.Token;
        var downloader = new SegmentDownloader(_networkClient, _fileStorage, _options.FlushThreshold);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!task.SupportsRanges) segment.BytesDownloaded = 0;
                await downloader.DownloadSegmentAsync(task.Url, task.TempFilePath, segment, token, task.SupportsRanges);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (RangeNotSupportedException)
            {
                failure.Cancel();
                throw;
            }
            catch (Exception) when (attempt < _options.MaxRetries)
            {
                segment.Status = DownloadStatus.Queued;
                await Task.Delay(_options.RetryDelay * (attempt + 1), token);
            }
            catch
            {
                failure.Cancel();
                throw;
            }
        }
    }

    private IReadOnlyList<DownloadSegment> CreateSegments(long totalBytes, int maxSegments)
    {
        var bySize = Math.Max(1, totalBytes / Math.Max(1, _options.MinSegmentSize));
        var count = (int)Math.Clamp(bySize, 1, Math.Max(1, maxSegments));
        var segmentSize = totalBytes / count;

        var segments = new DownloadSegment[count];
        for (var i = 0; i < count; i++)
        {
            var start = i * segmentSize;
            var end = i == count - 1 ? totalBytes - 1 : start + segmentSize - 1;
            segments[i] = new DownloadSegment { Index = i, StartOffset = start, EndOffset = end };
        }
        return segments;
    }
}
