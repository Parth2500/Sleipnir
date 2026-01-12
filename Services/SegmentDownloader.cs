using System;
using System.IO;
using System.Threading.Tasks;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Services;

public class SegmentDownloader
{
    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private const int BufferSize = 8192;

    public SegmentDownloader(INetworkClient networkClient, IFileStorage fileStorage)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
    }

    public async Task DownloadSegmentAsync(string url, string destinationPath, DownloadSegment segment, CancellationToken cancellationToken = default)
    {
        try
        {
            segment.Status = DownloadStatus.Downloading;
            
            // Calculate where to start downloading from
            var currentStart = segment.StartOffset + segment.BytesDownloaded;
            
            if (currentStart > segment.EndOffset)
            {
                segment.Status = DownloadStatus.Completed;
                return;
            }

            using var stream = await _networkClient.GetStreamAsync(url, currentStart, segment.EndOffset);
            
            // Read buffer
            var buffer = new byte[BufferSize];
            int bytesRead;
            var memoryBuffer = new MemoryStream();
            long totalBytesReadInThisSession = 0; // Track bytes read in THIS download session only
            long remainingBytes = segment.TotalBytes - segment.BytesDownloaded; // How many bytes left to download

            while (remainingBytes > 0 && (bytesRead = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remainingBytes), cancellationToken)) > 0)
            {
                // Check cancellation first
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                
                if (segment.Status == DownloadStatus.Paused || segment.Status == DownloadStatus.Cancelled)
                {
                    break;
                }

                await memoryBuffer.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesReadInThisSession += bytesRead;
                
                // Update the segment's total downloaded bytes
                segment.BytesDownloaded = (currentStart - segment.StartOffset) + totalBytesReadInThisSession;
                
                // Update remaining bytes
                remainingBytes = segment.TotalBytes - segment.BytesDownloaded;

                // Flush logic (similar to previous single threaded, but now per segment)
                if (memoryBuffer.Length >= 512 * 1024) // 512KB flush
                {
                    memoryBuffer.Position = 0;
                    await _fileStorage.WriteAsync(destinationPath, currentStart + totalBytesReadInThisSession - memoryBuffer.Length, memoryBuffer);
                    memoryBuffer.SetLength(0); 
                }
            }

            // Final flush
            if (memoryBuffer.Length > 0 && !cancellationToken.IsCancellationRequested)
            {
                memoryBuffer.Position = 0;
                await _fileStorage.WriteAsync(destinationPath, currentStart + totalBytesReadInThisSession - memoryBuffer.Length, memoryBuffer);
            }

            if (segment.Status != DownloadStatus.Cancelled && segment.BytesDownloaded >= segment.TotalBytes)
            {
                segment.Status = DownloadStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when download is paused/cancelled
            segment.Status = segment.Status == DownloadStatus.Cancelled ? DownloadStatus.Cancelled : DownloadStatus.Paused;
        }
        catch (Exception)
        {
            segment.Status = DownloadStatus.Failed;
            throw;
        }
    }
}
