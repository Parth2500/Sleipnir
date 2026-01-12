using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Services;

public class DownloadService : IDownloadService
{
    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private readonly ConcurrentDictionary<Guid, DownloadTask> _downloads = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _downloadCancellations = new();
    private const int BufferSize = 8192; // 8KB buffer

    public DownloadService(INetworkClient networkClient, IFileStorage fileStorage)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
    }

    public async Task<Guid> AddDownloadAsync(string url, string outputDirectory)
    {
        var uri = new Uri(url);
        string filename;
        
        // Try to extract filename from query parameters first (e.g., ?filename=xyz)
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var filenameParam = queryParams["filename"];
        
        if (!string.IsNullOrEmpty(filenameParam))
        {
            // Check if there's an extension parameter
            var extParam = queryParams["ext"];
            filename = !string.IsNullOrEmpty(extParam) ? $"{filenameParam}.{extParam}" : filenameParam;
        }
        else
        {
            // Fallback to path-based extraction
            filename = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(filename) || filename == "/")
            {
                filename = "downloaded_file";
            }
        }
        
        var destinationPath = Path.Combine(outputDirectory, filename);

        // 1. Create Task Entry
        var downloadTask = new DownloadTask
        {
            Url = url,
            DestinationPath = destinationPath, // This is the FINAL destination
            Filename = filename,
            Status = DownloadStatus.Queued
        };

        _downloads.TryAdd(downloadTask.Id, downloadTask);
        
        var cts = new CancellationTokenSource();
        _downloadCancellations[downloadTask.Id] = cts;
        _ = StartDownloadLoop(downloadTask, cts.Token);

        return downloadTask.Id;
    }

    public Task PauseDownloadAsync(Guid downloadId)
    {
        if (_downloads.TryGetValue(downloadId, out var task))
        {
            task.Status = DownloadStatus.Paused;
            foreach (var segment in task.Segments)
            {
                if (segment.Status == DownloadStatus.Downloading || segment.Status == DownloadStatus.Queued)
                {
                    segment.Status = DownloadStatus.Paused;
                }
            }
            
            // Cancel the running download loop
            if (_downloadCancellations.TryGetValue(downloadId, out var cts))
            {
                cts.Cancel();
            }
        }
        return Task.CompletedTask;
    }

    public Task ResumeDownloadAsync(Guid downloadId)
    {
        if (_downloads.TryGetValue(downloadId, out var task))
        {
             if(task.Status == DownloadStatus.Paused)
             {
                 task.Status = DownloadStatus.Queued;
                 
                 // Cancel any existing download loop (shouldn't exist, but safety check)
                 if (_downloadCancellations.TryRemove(downloadId, out var oldCts))
                 {
                     oldCts.Dispose();
                 }
                 
                 // Create new cancellation token and restart
                 var newCts = new CancellationTokenSource();
                 _downloadCancellations[downloadId] = newCts;
                 _ = StartDownloadLoop(task, newCts.Token); 
             }
        }
        return Task.CompletedTask;
    }

    private async Task StartDownloadLoop(DownloadTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            task.Status = DownloadStatus.Downloading;
            var tempFilePath = task.DestinationPath + ".dnm"; // Temporary file path
            
            // 1. Get Metadata (Only if not already known)
            if (task.TotalBytes == 0)
            {
                task.TotalBytes = await _networkClient.GetFileSizeAsync(task.Url);
            }

            // 2. Allocate File (Only if not already created - simple check)
            if (!_fileStorage.Exists(tempFilePath))
            {
                await _fileStorage.CreateFileAsync(tempFilePath, task.TotalBytes);
            }

            // 3. Segment Calculation (Only if segments don't exist)
            if (task.Segments.Count == 0)
            {
                int segmentCount = 8; 
                long segmentSize = task.TotalBytes / segmentCount;
                
                for (int i = 0; i < segmentCount; i++)
                {
                    var start = i * segmentSize;
                    var end = (i == segmentCount - 1) ? task.TotalBytes - 1 : (start + segmentSize - 1);
                    
                    task.Segments.Add(new DownloadSegment
                    {
                        Index = i,
                        StartOffset = start,
                        EndOffset = end,
                        Status = DownloadStatus.Queued
                    });
                }
            }

            // 4. Download Segments in Parallel
            var downloaders = task.Segments
                .Where(s => s.Status != DownloadStatus.Completed)
                .Select(segment => 
                {
                    if (segment.Status == DownloadStatus.Paused || segment.Status == DownloadStatus.Queued)
                    {
                        segment.Status = DownloadStatus.Downloading; 
                    }
                    var downloader = new SegmentDownloader(_networkClient, _fileStorage);
                    return downloader.DownloadSegmentAsync(task.Url, tempFilePath, segment, cancellationToken);
                });

            var downloadTask = Task.WhenAll(downloaders);
            var monitorTask = MonitorProgress(task, cancellationToken);
            
            await Task.WhenAny(downloadTask, monitorTask);
            
            // Check if cancelled
            if (cancellationToken.IsCancellationRequested)
            {
                return; // Exit cleanly
            }
            
            await downloadTask; 

            if (task.Status != DownloadStatus.Cancelled && task.Status != DownloadStatus.Paused)
            {
                // Verify all segments are done
                if (task.Segments.All(x => x.Status == DownloadStatus.Completed))
                {
                    // Finalization: Rename .dnm to .dest
                    if (_fileStorage.Exists(task.DestinationPath))
                    {
                        await _fileStorage.DeleteAsync(task.DestinationPath);
                    }
                    
                    File.Move(tempFilePath, task.DestinationPath);
                    task.Status = DownloadStatus.Completed;
                }
            }
            
            // Cleanup cancellation token
            _downloadCancellations.TryRemove(task.Id, out _);
        }
        catch (OperationCanceledException)
        {
            // Download was cancelled/paused - this is expected
            _downloadCancellations.TryRemove(task.Id, out _);
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            _downloadCancellations.TryRemove(task.Id, out _);
        }
    }

    private async Task MonitorProgress(DownloadTask task, CancellationToken cancellationToken = default)
    {
        while (task.Status == DownloadStatus.Downloading && !cancellationToken.IsCancellationRequested)
        {
            long total = 0;
            foreach (var seg in task.Segments)
            {
                total += seg.BytesDownloaded;
            }
            task.BytesDownloaded = total;
            await Task.Delay(100, cancellationToken);
            
            if (task.Segments.All(s => s.Status == DownloadStatus.Completed))
            {
                break;
            }
        }
    }

    public Task CancelDownloadAsync(Guid downloadId)
    {
        if (_downloads.TryGetValue(downloadId, out var task))
        {
            task.Status = DownloadStatus.Cancelled;
            foreach (var segment in task.Segments)
            {
                if (segment.Status == DownloadStatus.Downloading || 
                    segment.Status == DownloadStatus.Queued || 
                    segment.Status == DownloadStatus.Paused)
                {
                    segment.Status = DownloadStatus.Cancelled;
                }
            }
        }
        return Task.CompletedTask;
    }

    public IEnumerable<DownloadTask> GetAllDownloads()
    {
        return _downloads.Values;
    }

    public DownloadTask? GetDownload(Guid downloadId)
    {
        _downloads.TryGetValue(downloadId, out var task);
        return task;
    }
}
