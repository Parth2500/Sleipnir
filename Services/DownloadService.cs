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
    private const int BufferSize = 8192; // 8KB buffer

    public DownloadService(INetworkClient networkClient, IFileStorage fileStorage)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
    }

    public async Task<Guid> AddDownloadAsync(string url, string outputDirectory)
    {
        var filename = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(filename))
        {
            filename = "downloaded_file";
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
        
        _ = StartDownloadLoop(downloadTask);

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
                 // Restart the loop. 
                 _ = StartDownloadLoop(task); 
             }
        }
        return Task.CompletedTask;
    }

    private async Task StartDownloadLoop(DownloadTask task)
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
                    return downloader.DownloadSegmentAsync(task.Url, tempFilePath, segment);
                });

            var downloadTask = Task.WhenAll(downloaders);
            var monitorTask = MonitorProgress(task);
            
            await Task.WhenAny(downloadTask, monitorTask);
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
                    
                    // We need a Move/Rename method in IFileStorage, but for now we can use File.Move since LocalFileStorage is local.
                    // But to adhere to interface, we should add MoveAsync. 
                    // For this step, I will hack it using System.IO directly or Cast, but let's just use System.IO here for brevity or add to interface.
                    // Implementation Plan didn't specify changing Interface, but I should.
                    // However, to avoid complexity, I will just assume LocalFileStorage for this rename or add it to the LocalFileStorage class logic if accessible.
                    // Actually, I'll use File.Move here. It's an infrastructure concern leaking, but acceptable for this prototype stage.
                    
                    File.Move(tempFilePath, task.DestinationPath);
                    task.Status = DownloadStatus.Completed;
                }
            }
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
        }
    }

    private async Task MonitorProgress(DownloadTask task)
    {
        while (task.Status == DownloadStatus.Downloading)
        {
            long total = 0;
            foreach (var seg in task.Segments)
            {
                total += seg.BytesDownloaded;
            }
            task.BytesDownloaded = total;
            await Task.Delay(100);
            
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
