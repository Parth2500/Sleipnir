using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Services;

/// <summary>
/// Downloads one <see cref="DownloadSegment"/> into its slot of the pre-allocated temp file.
/// </summary>
public class SegmentDownloader
{
    private const int ReadChunkSize = 81920;

    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private readonly int _flushThreshold;

    public SegmentDownloader(INetworkClient networkClient, IFileStorage fileStorage, int flushThreshold = 512 * 1024)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
        _flushThreshold = flushThreshold;
    }

    /// <summary>
    /// Downloads the rest of <paramref name="segment"/>, resuming from <see cref="DownloadSegment.BytesDownloaded"/>.
    /// </summary>
    /// <param name="useRange">
    /// False for servers without Range support: the whole body is requested, so the segment must start at 0
    /// and be the only one.
    /// </param>
    /// <remarks>
    /// Invariant: whenever this method exits (success, pause/cancel, or error), <see cref="DownloadSegment.BytesDownloaded"/>
    /// equals the number of bytes actually written to disk, so a later resume never skips data.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled (pause/cancel). Received bytes are kept.</exception>
    /// <exception cref="IOException">The server closed the connection before the segment was complete.</exception>
    public async Task DownloadSegmentAsync(
        string url,
        string destinationPath,
        DownloadSegment segment,
        CancellationToken cancellationToken = default,
        bool useRange = true)
    {
        if (segment.BytesDownloaded >= segment.TotalBytes)
        {
            segment.Status = DownloadStatus.Completed;
            return;
        }

        segment.Status = DownloadStatus.Downloading;
        var persisted = segment.BytesDownloaded;
        using var pending = new MemoryStream();

        async Task FlushAsync()
        {
            if (pending.Length == 0) return;
            pending.Position = 0;
            // Never cancel a flush half-way: these bytes are already counted as received.
            await _fileStorage.WriteAsync(destinationPath, segment.StartOffset + persisted, pending, CancellationToken.None);
            persisted += pending.Length;
            pending.SetLength(0);
        }

        async Task SalvageAsync()
        {
            try { await FlushAsync(); }
            catch { /* disk write failed: those bytes will be re-downloaded */ }
            segment.BytesDownloaded = persisted;
        }

        try
        {
            long? from = useRange ? segment.StartOffset + persisted : null;
            long? to = useRange ? segment.EndOffset : null;
            await using var stream = await _networkClient.GetStreamAsync(url, from, to, cancellationToken);

            var chunk = new byte[ReadChunkSize];
            while (persisted + pending.Length < segment.TotalBytes)
            {
                var wanted = (int)Math.Min(chunk.Length, segment.TotalBytes - persisted - pending.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken);
                if (read == 0) break;

                pending.Write(chunk, 0, read);
                segment.BytesDownloaded = persisted + pending.Length; // live progress for the UI

                if (pending.Length >= _flushThreshold)
                {
                    await FlushAsync();
                }
            }

            await FlushAsync();
            segment.BytesDownloaded = persisted;

            if (persisted < segment.TotalBytes)
            {
                throw new IOException(
                    $"Connection closed early: segment {segment.Index + 1} received {persisted} of {segment.TotalBytes} bytes.");
            }

            segment.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SalvageAsync();
            segment.Status = DownloadStatus.Paused;
            throw;
        }
        catch
        {
            await SalvageAsync();
            segment.Status = DownloadStatus.Failed;
            throw;
        }
    }
}
