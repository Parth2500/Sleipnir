using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;

namespace Sleipnir.Services;

/// <summary>
/// Downloads one <see cref="DownloadSegment"/> into its slot of the temp file.
/// </summary>
public class SegmentDownloader
{
    private const int ReadChunkSize = 81920;

    private readonly INetworkClient _networkClient;
    private readonly IFileStorage _fileStorage;
    private readonly int _flushThreshold;
    private readonly TimeSpan _stallTimeout;

    public SegmentDownloader(
        INetworkClient networkClient,
        IFileStorage fileStorage,
        int flushThreshold = 512 * 1024,
        TimeSpan? stallTimeout = null)
    {
        _networkClient = networkClient;
        _fileStorage = fileStorage;
        _flushThreshold = flushThreshold;
        _stallTimeout = stallTimeout is { } t && t > TimeSpan.Zero ? t : Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Downloads the rest of <paramref name="segment"/>, resuming from the bytes already on disk.
    /// </summary>
    /// <param name="useRange">
    /// False for servers without Range support: the whole body is requested, so the segment must start at 0
    /// and be the only one.
    /// </param>
    /// <remarks>
    /// Invariants:
    /// <list type="bullet">
    /// <item><see cref="DownloadSegment.PersistedBytes"/> only ever counts bytes already written to disk.</item>
    /// <item>When this method exits for any reason, <see cref="DownloadSegment.BytesDownloaded"/> equals
    /// <see cref="DownloadSegment.PersistedBytes"/>, so a resume never skips data.</item>
    /// </list>
    /// An open-ended segment (unknown file size) reads until the server closes the stream, then fixes its
    /// <see cref="DownloadSegment.EndOffset"/> to the real length.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled (pause/cancel). Received bytes are kept.</exception>
    /// <exception cref="IOException">The server closed the connection before the segment was complete.</exception>
    /// <exception cref="TimeoutException">No data arrived for longer than the stall timeout.</exception>
    public async Task DownloadSegmentAsync(
        string url,
        string destinationPath,
        DownloadSegment segment,
        CancellationToken cancellationToken = default,
        bool useRange = true)
    {
        var persisted = Math.Min(segment.BytesDownloaded, segment.PersistedBytes);
        segment.PersistedBytes = persisted;
        segment.BytesDownloaded = persisted;

        if (!segment.IsOpenEnded && persisted >= segment.TotalBytes)
        {
            segment.Status = DownloadStatus.Completed;
            return;
        }

        segment.Status = DownloadStatus.Downloading;
        using var pending = new MemoryStream();
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task FlushAsync()
        {
            if (pending.Length == 0) return;
            pending.Position = 0;
            // Never cancel a flush half-way: these bytes are already counted as received.
            await _fileStorage.WriteAsync(destinationPath, segment.StartOffset + persisted, pending, CancellationToken.None);
            persisted += pending.Length;
            segment.PersistedBytes = persisted;
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
            long? to = useRange && !segment.IsOpenEnded ? segment.EndOffset : null;
            await using var stream = await _networkClient.GetStreamAsync(url, from, to, cancellationToken);

            var chunk = new byte[ReadChunkSize];
            while (segment.IsOpenEnded || persisted + pending.Length < segment.TotalBytes)
            {
                var wanted = segment.IsOpenEnded
                    ? chunk.Length
                    : (int)Math.Min(chunk.Length, segment.TotalBytes - persisted - pending.Length);

                stall.CancelAfter(_stallTimeout); // restart the idle timer for every read
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), stall.Token);
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

            if (segment.IsOpenEnded)
            {
                segment.EndOffset = segment.StartOffset + persisted - 1;
            }
            else if (persisted < segment.TotalBytes)
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
        catch (OperationCanceledException ex) when (stall.IsCancellationRequested)
        {
            await SalvageAsync();
            segment.Status = DownloadStatus.Failed;
            throw new TimeoutException(
                $"No data received for {_stallTimeout.TotalSeconds:0.#} s on segment {segment.Index + 1}.", ex);
        }
        catch
        {
            await SalvageAsync();
            segment.Status = DownloadStatus.Failed;
            throw;
        }
    }
}
