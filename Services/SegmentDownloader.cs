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

    public async Task DownloadSegmentAsync(string url, string destinationPath, DownloadSegment segment)
    {
        try
        {
            segment.Status = DownloadStatus.Downloading;
            
            // Resume support: Start from where we left off if partially downloaded
            // For now, simple start from beginning of segment
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
            long totalBytesReadInLoop = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (segment.Status == DownloadStatus.Paused || segment.Status == DownloadStatus.Cancelled)
                {
                    break;
                }

                await memoryBuffer.WriteAsync(buffer, 0, bytesRead);
                segment.BytesDownloaded += bytesRead;
                totalBytesReadInLoop += bytesRead;

                // Flush logic (similar to previous single threaded, but now per segment)
                 if (memoryBuffer.Length >= 512 * 1024) // 512KB flush
                {
                    memoryBuffer.Position = 0;
                    await _fileStorage.WriteAsync(destinationPath, currentStart + totalBytesReadInLoop - memoryBuffer.Length, memoryBuffer);
                    memoryBuffer.SetLength(0); 
                }
            }

            // Final flush
            if (memoryBuffer.Length > 0)
            {
                memoryBuffer.Position = 0;
                await _fileStorage.WriteAsync(destinationPath, currentStart + totalBytesReadInLoop - memoryBuffer.Length, memoryBuffer);
            }

            if (segment.Status != DownloadStatus.Cancelled && segment.BytesDownloaded >= segment.TotalBytes)
            {
                segment.Status = DownloadStatus.Completed;
            }
        }
        catch (Exception)
        {
            segment.Status = DownloadStatus.Failed;
            throw;
        }
    }
}
