using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.Tests.Fakes;

namespace DotNetDownloadManager.Tests;

public class SegmentDownloaderTests
{
    private const string Url = "http://example.test/file.bin";

    [Fact]
    public async Task Pausing_mid_segment_keeps_every_received_byte_so_resume_produces_an_intact_file()
    {
        using var dir = new TempDir();
        var path = dir.File("file.bin.dnm");
        var data = TestHelpers.RandomBytes(100_000);
        var storage = new LocalFileStorage();
        await storage.CreateFileAsync(path, data.Length);
        var segment = new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = data.Length - 1 };

        // First attempt: server sends 40 KB (below the 512 KB flush threshold) and then stalls.
        FakeNetworkClient? stalling = null;
        stalling = new FakeNetworkClient(data) { OnRequest = (_, from, _) => stalling!.Stalling(from, 40_000) };
        using var cts = new CancellationTokenSource();
        var first = new SegmentDownloader(stalling, storage).DownloadSegmentAsync(Url, path, segment, cts.Token);
        Assert.True(await TestHelpers.WaitUntil(() => segment.BytesDownloaded == 40_000));
        cts.Cancel(); // user presses Pause
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // Second attempt: resume from where the segment says it stopped.
        await new SegmentDownloader(new FakeNetworkClient(data), storage).DownloadSegmentAsync(Url, path, segment);

        Assert.Equal(DownloadStatus.Completed, segment.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Connection_closed_before_segment_end_is_reported_as_an_error()
    {
        using var dir = new TempDir();
        var path = dir.File("file.bin.dnm");
        var data = TestHelpers.RandomBytes(10_000);
        var storage = new LocalFileStorage();
        await storage.CreateFileAsync(path, data.Length);
        var segment = new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = data.Length - 1 };
        var truncating = new FakeNetworkClient(data)
        {
            OnRequest = (_, _, _) => new MemoryStream(data, 0, 4_000) // server hangs up after 4 KB
        };

        await Assert.ThrowsAsync<IOException>(
            () => new SegmentDownloader(truncating, storage).DownloadSegmentAsync(Url, path, segment));

        Assert.Equal(DownloadStatus.Failed, segment.Status);
        Assert.Equal(4_000, segment.BytesDownloaded);
    }
}
