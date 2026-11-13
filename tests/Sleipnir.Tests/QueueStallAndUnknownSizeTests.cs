using Sleipnir.Core.Models;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;
using Sleipnir.Tests.Fakes;

namespace Sleipnir.Tests;

public class QueueStallAndUnknownSizeTests
{
    private static string Url(int n) => $"http://example.test/files/file{n}.bin";

    private static FakeNetworkClient StallingNetwork(byte[] data)
    {
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(data) { OnRequest = (_, from, _) => network!.Stalling(from, 1_000) };
        return network;
    }

    private static DownloadStatus StatusOf(DownloadService s, Guid id) => s.GetDownload(id)!.Status;

    [Fact]
    public async Task Only_MaxConcurrentDownloads_run_at_once_and_the_rest_wait_their_turn()
    {
        using var dir = new TempDir();
        var service = new DownloadService(StallingNetwork(TestHelpers.RandomBytes(10_000)), new LocalFileStorage(),
            new DownloadOptions { MaxConcurrentDownloads = 2, SegmentCount = 1 });

        var a = await service.AddDownloadAsync(Url(1), dir.Path);
        var b = await service.AddDownloadAsync(Url(2), dir.Path);
        var c = await service.AddDownloadAsync(Url(3), dir.Path);

        Assert.True(await TestHelpers.WaitUntil(() => StatusOf(service, a) == DownloadStatus.Downloading && StatusOf(service, b) == DownloadStatus.Downloading));
        await Task.Delay(150);
        Assert.Equal(DownloadStatus.Queued, StatusOf(service, c));

        await service.CancelDownloadAsync(a);

        Assert.True(await TestHelpers.WaitUntil(() => StatusOf(service, c) == DownloadStatus.Downloading), "queued download did not start when a slot freed up");
    }

    [Fact]
    public async Task Pausing_a_queued_download_keeps_it_from_starting()
    {
        using var dir = new TempDir();
        var service = new DownloadService(StallingNetwork(TestHelpers.RandomBytes(10_000)), new LocalFileStorage(),
            new DownloadOptions { MaxConcurrentDownloads = 1, SegmentCount = 1 });

        var a = await service.AddDownloadAsync(Url(1), dir.Path);
        var b = await service.AddDownloadAsync(Url(2), dir.Path);
        await service.PauseDownloadAsync(b);
        Assert.Equal(DownloadStatus.Paused, StatusOf(service, b));

        await service.CancelDownloadAsync(a);
        await Task.Delay(200);

        Assert.Equal(DownloadStatus.Paused, StatusOf(service, b));
    }

    [Fact]
    public async Task Stalled_connection_is_abandoned_and_retried()
    {
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(50_000);
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(data)
        {
            // First request sends 1 KB then goes silent forever; the retry is healthy.
            OnRequest = (n, from, _) => n == 0 ? network!.Stalling(from, 1_000) : null,
        };
        var service = new DownloadService(network, new LocalFileStorage(), new DownloadOptions
        {
            SegmentCount = 1,
            RetryDelay = TimeSpan.Zero,
            StallTimeout = TimeSpan.FromMilliseconds(200),
        });

        var id = await service.AddDownloadAsync(Url(1), dir.Path);

        Assert.True(await TestHelpers.WaitUntil(() => StatusOf(service, id) == DownloadStatus.Completed), $"status {StatusOf(service, id)}: {service.GetDownload(id)!.ErrorMessage}");
        Assert.Equal(data, await File.ReadAllBytesAsync(service.GetDownload(id)!.DestinationPath));
    }

    [Fact]
    public async Task Download_of_unknown_size_reads_until_the_server_closes_the_stream()
    {
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(300_000);
        var network = new FakeNetworkClient(data) { ReportSize = false, SupportsRanges = false, HonorRanges = false };
        var service = new DownloadService(network, new LocalFileStorage(), new DownloadOptions());

        var id = await service.AddDownloadAsync(Url(1), dir.Path);

        Assert.True(await TestHelpers.WaitUntil(() => StatusOf(service, id) is DownloadStatus.Completed or DownloadStatus.Failed));
        var task = service.GetDownload(id)!;
        Assert.True(task.Status == DownloadStatus.Completed, task.ErrorMessage);
        Assert.Equal(data, await File.ReadAllBytesAsync(task.DestinationPath));
        Assert.Equal(data.Length, task.TotalBytes);
        Assert.Single(task.Segments);
    }

    [Fact]
    public async Task Unknown_size_download_that_fails_midway_restarts_cleanly()
    {
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(300_000);
        var network = new FakeNetworkClient(data)
        {
            ReportSize = false, SupportsRanges = false, HonorRanges = false,
            OnRequest = (n, _, _) => n == 0 ? new FailingStream(data, failAfter: 200_000) : null,
        };
        var service = new DownloadService(network, new LocalFileStorage(), new DownloadOptions { RetryDelay = TimeSpan.Zero });

        var id = await service.AddDownloadAsync(Url(1), dir.Path);

        Assert.True(await TestHelpers.WaitUntil(() => StatusOf(service, id) is DownloadStatus.Completed or DownloadStatus.Failed));
        Assert.Equal(DownloadStatus.Completed, StatusOf(service, id));
        Assert.Equal(data, await File.ReadAllBytesAsync(service.GetDownload(id)!.DestinationPath));
    }

    /// <summary>Serves the first <c>failAfter</c> bytes, then throws like a reset connection.</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter) throw new IOException("connection reset by peer");
            var n = Math.Min(count, failAfter - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
