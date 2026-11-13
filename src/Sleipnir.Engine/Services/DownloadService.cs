using System.Collections.Concurrent;
using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;

namespace Sleipnir.Services;

/// <summary>
/// Orchestrates downloads: queues them, probes the server, splits files into segments, runs segments in
/// parallel with retries, handles pause / resume / cancel, and saves progress so downloads survive a restart.
/// </summary>
public class DownloadService : IDownloadService
{
    /// <summary>Finished (completed/cancelled) downloads kept in history across restarts.</summary>
    private const int HistoryLimit = 200;

    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private readonly DownloadOptions _options;
    private readonly IDownloadStateStore? _stateStore;
    private readonly ConcurrentDictionary<Guid, DownloadTask> _downloads = new();
    private readonly ConcurrentDictionary<Guid, DownloadRun> _runs = new();
    private readonly List<Guid> _queue = new(); // waiting downloads, FIFO; guarded by _gate
    private readonly object _gate = new();
    private bool _shuttingDown;

    public DownloadService(
        INetworkClient networkClient,
        IFileStorage fileStorage,
        DownloadOptions? options = null,
        IDownloadStateStore? stateStore = null)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
        _options = options ?? new DownloadOptions();
        _stateStore = stateStore;
    }

    private enum StopReason { None, Pause, Cancel, Shutdown }

    /// <summary>One execution of a download, from start/resume until it finishes, pauses, or is cancelled.</summary>
    private sealed class DownloadRun
    {
        public CancellationTokenSource Cts { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public volatile StopReason Reason = StopReason.None;
    }

    public async Task<Guid> AddDownloadAsync(string url, string outputDirectory, Guid? ownerId = null)
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
                OwnerId = ownerId,
                DestinationPath = destination,
                Filename = Path.GetFileName(destination),
                Status = DownloadStatus.Queued,
            };
            _downloads[task.Id] = task;
            _queue.Add(task.Id);
            PumpQueue();
        }

        await SaveAsync(task);
        return task.Id;
    }

    public async Task PauseDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;

        lock (_gate)
        {
            if (_queue.Remove(downloadId))
            {
                task.Status = DownloadStatus.Paused;
            }
        }

        if (task.Status == DownloadStatus.Paused && !_runs.ContainsKey(downloadId))
        {
            await SaveAsync(task);
            return;
        }

        await StopRunAsync(downloadId, StopReason.Pause);
    }

    public async Task ResumeDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;

        // Make sure the previous run has fully exited first, so two runs never write the same file.
        if (_runs.TryGetValue(downloadId, out var previous))
        {
            await previous.Completion;
        }

        lock (_gate)
        {
            if (task.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
            if (_runs.ContainsKey(downloadId) || _queue.Contains(downloadId)) return;

            task.Status = DownloadStatus.Queued;
            task.ErrorMessage = null;
            _queue.Add(downloadId);
            PumpQueue();
        }

        await SaveAsync(task);
    }

    public async Task CancelDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;
        if (task.Status == DownloadStatus.Completed) return;

        lock (_gate)
        {
            _queue.Remove(downloadId);
        }

        await StopRunAsync(downloadId, StopReason.Cancel);

        task.Status = DownloadStatus.Cancelled;
        foreach (var segment in task.Segments)
        {
            if (segment.Status != DownloadStatus.Completed)
            {
                segment.Status = DownloadStatus.Cancelled;
            }
        }

        await _fileStorage.DeleteAsync(task.TempFilePath);
        await SaveAsync(task);
    }

    public async Task RemoveDownloadAsync(Guid downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var task)) return;
        if (task.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled)) return;

        lock (_gate)
        {
            if (_runs.ContainsKey(downloadId)) return;
            _queue.Remove(downloadId);
            _downloads.TryRemove(downloadId, out _);
        }

        if (_stateStore is not null) await _stateStore.DeleteAsync(downloadId);
    }

    public async Task RestoreAsync()
    {
        if (_stateStore is null) return;

        var records = (await _stateStore.LoadAllAsync()).OrderBy(r => r.CreatedAt).ToList();

        // Keep history bounded: drop the oldest finished downloads beyond the limit.
        var finished = records.Where(r => r.Status is DownloadStatus.Completed or DownloadStatus.Cancelled).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - HistoryLimit)))
        {
            await _stateStore.DeleteAsync(old.Id);
            records.Remove(old);
        }

        lock (_gate)
        {
            foreach (var record in records)
            {
                if (_downloads.ContainsKey(record.Id)) continue;

                var task = FromRecord(record);
                _downloads[task.Id] = task;

                // Downloads that were running (or waiting) when the app stopped are queued again.
                if (task.Status == DownloadStatus.Queued)
                {
                    _queue.Add(task.Id);
                }
            }
            PumpQueue();
        }
    }

    public async Task ShutdownAsync()
    {
        List<DownloadRun> running;
        lock (_gate)
        {
            _shuttingDown = true;
            running = _runs.Values.ToList();
            foreach (var run in running)
            {
                run.Reason = StopReason.Shutdown;
                run.Cts.Cancel();
            }
        }

        await Task.WhenAll(running.Select(r => r.Completion));

        foreach (var task in _downloads.Values)
        {
            await SaveAsync(task);
        }
    }

    public IEnumerable<DownloadTask> GetAllDownloads() => _downloads.Values;

    public DownloadTask? GetDownload(Guid downloadId) => _downloads.TryGetValue(downloadId, out var task) ? task : null;

    // Must be called while holding _gate.
    private void PumpQueue()
    {
        while (!_shuttingDown && _runs.Count < Math.Max(1, _options.MaxConcurrentDownloads) && _queue.Count > 0)
        {
            var id = _queue[0];
            _queue.RemoveAt(0);
            if (_downloads.TryGetValue(id, out var task) && task.Status == DownloadStatus.Queued && !_runs.ContainsKey(id))
            {
                var run = new DownloadRun();
                _runs[id] = run;
                run.Completion = Task.Run(() => RunAsync(task, run));
            }
        }
    }

    private async Task StopRunAsync(Guid downloadId, StopReason reason)
    {
        DownloadRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(downloadId, out run)) return;
            run.Reason = reason;
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
        DownloadStatus finalStatus;
        using var stopSaving = new CancellationTokenSource();
        var periodicSave = Task.CompletedTask;

        try
        {
            task.Status = DownloadStatus.Downloading;
            task.ErrorMessage = null;

            // 1. Probe the server once per download.
            if (task.Segments.Count == 0 && task.TotalBytes == 0 && task.IsSizeKnown)
            {
                var info = await ProbeWithRetriesAsync(task.Url, token);
                task.IsSizeKnown = info.Size.HasValue;
                task.TotalBytes = info.Size ?? 0;
                // Without a size we cannot split the file, so it is always a single stream.
                task.SupportsRanges = info.SupportsRanges && info.Size.HasValue;
            }

            // 2. (Re)create the temp file if it is missing; any recorded progress is then invalid.
            if (!_fileStorage.Exists(task.TempFilePath))
            {
                await _fileStorage.CreateFileAsync(task.TempFilePath, task.TotalBytes);
                task.Segments = Array.Empty<DownloadSegment>();
            }

            await SaveAsync(task);
            if (_stateStore is not null)
            {
                periodicSave = SavePeriodicallyAsync(task, stopSaving.Token);
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

            if (!task.IsSizeKnown)
            {
                task.TotalBytes = task.BytesDownloaded;
                task.IsSizeKnown = true;
            }

            // 4. Move the finished temp file into place without overwriting anything.
            string destination;
            lock (_gate)
            {
                destination = _fileStorage.Exists(task.DestinationPath)
                    ? FileNameResolver.MakeUnique(Path.GetDirectoryName(task.DestinationPath)!, task.Filename, IsPathTaken)
                    : task.DestinationPath;
            }
            _fileStorage.Move(task.TempFilePath, destination);
            task.DestinationPath = destination;
            task.Filename = Path.GetFileName(destination);
            finalStatus = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            finalStatus = run.Reason switch
            {
                StopReason.Cancel => DownloadStatus.Cancelled,
                StopReason.Shutdown => DownloadStatus.Queued, // resume automatically next session
                _ => DownloadStatus.Paused,
            };
        }
        catch (Exception ex)
        {
            finalStatus = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
        }

        stopSaving.Cancel();
        try { await periodicSave; } catch (OperationCanceledException) { }

        // Save before publishing the final status, so anyone who sees it can rely on the saved state.
        await SaveAsync(task, finalStatus);
        task.Status = finalStatus;

        lock (_gate)
        {
            // Only remove our own entry; a newer run may already be registered.
            _runs.TryRemove(new KeyValuePair<Guid, DownloadRun>(task.Id, run));
            PumpQueue();
        }
        run.Cts.Dispose();
    }

    private async Task SavePeriodicallyAsync(DownloadTask task, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(_options.StateSaveInterval, token);
            await SaveAsync(task);
        }
    }

    private async Task DownloadSegmentsAsync(DownloadTask task, CancellationToken token)
    {
        if (task.Segments.Count == 0)
        {
            task.Segments = CreateSegments(task);
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
        var downloader = new SegmentDownloader(_networkClient, _fileStorage, _options.FlushThreshold, _options.StallTimeout);
        var retries = new RetryBudget();

        while (true)
        {
            try
            {
                if (!task.SupportsRanges)
                {
                    // Without ranges a single stream must start from byte 0 every time.
                    segment.BytesDownloaded = 0;
                    segment.PersistedBytes = 0;
                    if (segment.IsOpenEnded)
                    {
                        await _fileStorage.CreateFileAsync(task.TempFilePath, 0); // drop any stale tail
                    }
                }

                await downloader.DownloadSegmentAsync(task.Url, task.TempFilePath, segment, token, task.SupportsRanges);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (NextRetryDelay(ex, retries) is not { } delay)
                {
                    failure.Cancel(); // permanent: stop the other segments too
                    throw;
                }
                segment.Status = DownloadStatus.Queued;
                await Task.Delay(delay, token);
            }
        }
    }

    private async Task<RemoteFileInfo> ProbeWithRetriesAsync(string url, CancellationToken token)
    {
        var retries = new RetryBudget();
        while (true)
        {
            try
            {
                return await _networkClient.GetFileInfoAsync(url, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (NextRetryDelay(ex, retries) is not { } delay) throw;
                await Task.Delay(delay, token);
            }
        }
    }

    /// <summary>Retries used so far by one operation; "server busy" replies have their own, larger budget.</summary>
    private sealed class RetryBudget
    {
        public int Attempts;
        public int BusyAttempts;
    }

    /// <summary>
    /// The retry policy: how long to wait before retrying after <paramref name="error"/>, or null to give up.
    /// </summary>
    private TimeSpan? NextRetryDelay(Exception error, RetryBudget budget)
    {
        switch (error)
        {
            case RangeNotSupportedException:
                return null; // handled by switching to single-stream mode
            case ServerBusyException busy when budget.BusyAttempts < _options.MaxBusyRetries:
                budget.BusyAttempts++;
                return BusyDelay(busy, budget.BusyAttempts);
            case HttpRequestException http when IsPermanent(http.StatusCode):
                return null; // 404, 403, 410…: asking again won't help
        }

        if (budget.Attempts >= _options.MaxRetries) return null;
        budget.Attempts++;
        return _options.RetryDelay * budget.Attempts;
    }

    /// <summary>4xx client errors are permanent, except timeouts and rate limiting.</summary>
    private static bool IsPermanent(System.Net.HttpStatusCode? status) =>
        status is { } code && (int)code is >= 400 and < 500 && (int)code is not (408 or 425 or 429);

    /// <summary>The server's Retry-After if given, else exponential backoff with jitter; capped at MaxBusyDelay.</summary>
    private TimeSpan BusyDelay(ServerBusyException busy, int busyAttempt)
    {
        var delay = busy.RetryAfter
            ?? TimeSpan.FromTicks((long)(_options.RetryDelay.Ticks * Math.Pow(2, busyAttempt) * (1 + Random.Shared.NextDouble() * 0.25)));
        return delay < _options.MaxBusyDelay ? delay : _options.MaxBusyDelay;
    }

    private IReadOnlyList<DownloadSegment> CreateSegments(DownloadTask task)
    {
        if (!task.IsSizeKnown)
        {
            return new[] { new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = long.MaxValue - 1, IsOpenEnded = true } };
        }

        var totalBytes = task.TotalBytes;
        var maxSegments = task.SupportsRanges ? _options.SegmentCount : 1;
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

    private async Task SaveAsync(DownloadTask task, DownloadStatus? status = null)
    {
        if (_stateStore is null) return;
        try
        {
            await _stateStore.SaveAsync(ToRecord(task, status ?? task.Status));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving state is best effort; the download itself keeps going.
        }
    }

    private static DownloadRecord ToRecord(DownloadTask task, DownloadStatus status) => new(
        task.Id,
        task.Url,
        task.DestinationPath,
        task.Filename,
        task.TotalBytes,
        task.IsSizeKnown,
        task.SupportsRanges,
        status,
        task.CreatedAt,
        task.ErrorMessage,
        task.Segments
            .Select(s => new SegmentRecord(s.Index, s.StartOffset, s.EndOffset, s.PersistedBytes, s.IsOpenEnded))
            .ToList(),
        task.OwnerId);

    private static DownloadTask FromRecord(DownloadRecord record)
    {
        var status = record.Status is DownloadStatus.Downloading ? DownloadStatus.Queued : record.Status;
        var finished = status is DownloadStatus.Completed;

        return new DownloadTask
        {
            Id = record.Id,
            OwnerId = record.OwnerId,
            Url = record.Url,
            DestinationPath = record.DestinationPath,
            Filename = record.Filename,
            TotalBytes = record.TotalBytes,
            IsSizeKnown = record.IsSizeKnown,
            SupportsRanges = record.SupportsRanges,
            Status = status,
            CreatedAt = record.CreatedAt,
            ErrorMessage = record.ErrorMessage,
            Segments = record.Segments.Select(s =>
            {
                var segment = new DownloadSegment
                {
                    Index = s.Index,
                    StartOffset = s.StartOffset,
                    EndOffset = s.EndOffset,
                    IsOpenEnded = s.IsOpenEnded,
                };
                var bytes = finished ? segment.TotalBytes : s.BytesDownloaded;
                segment.BytesDownloaded = bytes;
                segment.PersistedBytes = bytes;
                segment.Status = finished || (!s.IsOpenEnded && bytes >= segment.TotalBytes)
                    ? DownloadStatus.Completed
                    : DownloadStatus.Paused;
                return segment;
            }).ToList(),
        };
    }
}
