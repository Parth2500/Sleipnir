using System.Net;
using Sleipnir.Core.Models;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;
using Sleipnir.Tests.Fakes;

namespace Sleipnir.Tests;

public class DownloadServiceTests
{
    private const string Url = "http://example.test/files/file.bin";

    private static readonly DownloadOptions FastOptions = new()
    {
        SegmentCount = 4,
        MinSegmentSize = 1,
        MaxRetries = 2,
        RetryDelay = TimeSpan.Zero,
    };

    private static DownloadService CreateService(FakeNetworkClient network) =>
        new(network, new LocalFileStorage(), FastOptions);

    private static bool IsFinished(DownloadTask? t) =>
        t is { Status: DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled };

    [Fact]
    public async Task Download_produces_a_file_identical_to_the_source()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(200_000));
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        var task = service.GetDownload(id)!;
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(task.DestinationPath));
        Assert.False(File.Exists(task.DestinationPath + ".dnm"));
        Assert.Equal(network.Data.Length, task.BytesDownloaded);
    }

    [Fact]
    public async Task Server_without_range_support_is_downloaded_as_a_single_stream()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(200_000)) { SupportsRanges = false, HonorRanges = false };
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        var task = service.GetDownload(id)!;
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(task.DestinationPath));
    }

    [Fact]
    public async Task Server_that_silently_ignores_ranges_still_produces_a_correct_file()
    {
        // Advertises range support but answers every request with the whole file (HTTP 200).
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(200_000)) { SupportsRanges = true, HonorRanges = false };
        var range = new RangeIgnoringNetworkClient(network);
        var service = new DownloadService(range, new LocalFileStorage(), FastOptions);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        var task = service.GetDownload(id)!;
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(task.DestinationPath));
    }

    [Fact]
    public async Task Pause_then_resume_then_pause_again_really_stops_the_download()
    {
        using var dir = new TempDir();
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(TestHelpers.RandomBytes(400_000))
        {
            OnRequest = (_, from, _) => network!.Stalling(from, 1_000)
        };
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => network.OpenStreams == 4));

        await service.PauseDownloadAsync(id);
        await service.ResumeDownloadAsync(id);
        Assert.True(await TestHelpers.WaitUntil(() => network.RequestCount >= 8 && service.GetDownload(id)!.Status == DownloadStatus.Downloading));
        await Task.Delay(100);

        await service.PauseDownloadAsync(id);

        Assert.True(await TestHelpers.WaitUntil(() => network.OpenStreams == 0), $"{network.OpenStreams} streams still open after pause");
        Assert.Equal(DownloadStatus.Paused, service.GetDownload(id)!.Status);
    }

    [Fact]
    public async Task Cancel_aborts_stalled_transfers_and_removes_the_partial_file()
    {
        using var dir = new TempDir();
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(TestHelpers.RandomBytes(400_000))
        {
            OnRequest = (_, from, _) => network!.Stalling(from, 1_000)
        };
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => network.OpenStreams == 4));

        await service.CancelDownloadAsync(id);

        Assert.True(await TestHelpers.WaitUntil(() => network.OpenStreams == 0), $"{network.OpenStreams} streams still open after cancel");
        var task = service.GetDownload(id)!;
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.True(await TestHelpers.WaitUntil(() => !File.Exists(task.DestinationPath + ".dnm")), "partial .dnm file left behind");
    }

    [Fact]
    public async Task Transient_segment_failure_is_retried()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(200_000))
        {
            OnRequest = (n, _, _) => n == 1 ? throw new HttpRequestException("connection reset") : null
        };
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        var task = service.GetDownload(id)!;
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(task.DestinationPath));
    }

    [Fact]
    public async Task Permanent_segment_failure_fails_the_download_and_stops_the_other_segments()
    {
        using var dir = new TempDir();
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(TestHelpers.RandomBytes(400_000))
        {
            OnRequest = (_, from, _) => from == 0
                ? throw new HttpRequestException("404 for this range")
                : network!.Stalling(from, 1_000)
        };
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.Equal(DownloadStatus.Failed, service.GetDownload(id)!.Status);
        Assert.Contains("404", service.GetDownload(id)!.ErrorMessage);
        Assert.True(await TestHelpers.WaitUntil(() => network.OpenStreams == 0), $"{network.OpenStreams} segments still downloading after failure");
    }

    [Fact]
    public async Task Busy_server_is_waited_out_beyond_the_normal_retry_budget()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(50_000))
        {
            // The server says "429, try again in 20 ms" five times before letting us in.
            OnRequest = (n, _, _) => n < 5 ? throw new Core.Interfaces.ServerBusyException(Url, 429, TimeSpan.FromMilliseconds(20)) : null,
        };
        var service = new DownloadService(network, new LocalFileStorage(),
            new DownloadOptions { SegmentCount = 1, MaxRetries = 1, RetryDelay = TimeSpan.Zero, MaxBusyRetries = 10 });

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.True(service.GetDownload(id)!.Status == DownloadStatus.Completed, service.GetDownload(id)!.ErrorMessage);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(service.GetDownload(id)!.DestinationPath));
    }

    [Fact]
    public async Task Probe_failures_are_retried_too()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(10_000))
        {
            OnProbe = n =>
            {
                if (n == 0) throw new Core.Interfaces.ServerBusyException(Url, 429, TimeSpan.FromMilliseconds(10));
                if (n == 1) throw new HttpRequestException("connection reset");
            },
        };
        var service = new DownloadService(network, new LocalFileStorage(),
            new DownloadOptions { SegmentCount = 1, MaxRetries = 2, RetryDelay = TimeSpan.Zero });

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.True(service.GetDownload(id)!.Status == DownloadStatus.Completed, service.GetDownload(id)!.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Permanent_http_errors_fail_immediately_without_retrying(HttpStatusCode status)
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(50_000))
        {
            OnRequest = (_, _, _) => throw new HttpRequestException($"HTTP {(int)status}", null, status),
        };
        var service = new DownloadService(network, new LocalFileStorage(),
            new DownloadOptions { SegmentCount = 1, MaxRetries = 3, RetryDelay = TimeSpan.Zero });

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.Equal(DownloadStatus.Failed, service.GetDownload(id)!.Status);
        Assert.Equal(1, network.RequestCount);
    }

    [Theory]
    [InlineData("http://example.test/get?filename=../../escaped.txt")]
    [InlineData("http://example.test/get?filename=..%2F..%2Fescaped.txt")]
    [InlineData("http://example.test/get?filename=/etc/escaped.txt")]
    public async Task Filename_from_query_string_cannot_escape_the_output_directory(string url)
    {
        using var dir = new TempDir();
        var service = CreateService(new FakeNetworkClient(TestHelpers.RandomBytes(1_000)));

        var id = await service.AddDownloadAsync(url, dir.Path);

        var task = service.GetDownload(id)!;
        Assert.Equal(Path.GetFullPath(dir.Path), Path.GetDirectoryName(Path.GetFullPath(task.DestinationPath)));
        Assert.Equal("escaped.txt", task.Filename);
    }

    [Fact]
    public async Task Existing_file_with_the_same_name_is_never_overwritten()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("file.bin"), "precious user data");
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(10_000));
        var service = CreateService(network);

        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.Equal("precious user data", await File.ReadAllTextAsync(dir.File("file.bin")));
        var task = service.GetDownload(id)!;
        Assert.Equal("file (1).bin", task.Filename);
        Assert.Equal(network.Data, await File.ReadAllBytesAsync(dir.File("file (1).bin")));
    }

    [Fact]
    public async Task Two_concurrent_downloads_of_the_same_url_get_different_files()
    {
        using var dir = new TempDir();
        var service = CreateService(new FakeNetworkClient(TestHelpers.RandomBytes(10_000)));

        var a = await service.AddDownloadAsync(Url, dir.Path);
        var b = await service.AddDownloadAsync(Url, dir.Path);

        Assert.NotEqual(service.GetDownload(a)!.DestinationPath, service.GetDownload(b)!.DestinationPath);
    }

    [Fact]
    public async Task Missing_output_directory_is_created()
    {
        using var dir = new TempDir();
        var output = Path.Combine(dir.Path, "does", "not", "exist");
        var service = CreateService(new FakeNetworkClient(TestHelpers.RandomBytes(10_000)));

        var id = await service.AddDownloadAsync(Url, output);
        Assert.True(await TestHelpers.WaitUntil(() => IsFinished(service.GetDownload(id))));

        Assert.Equal(DownloadStatus.Completed, service.GetDownload(id)!.Status);
        Assert.True(File.Exists(Path.Combine(output, "file.bin")));
    }

    /// <summary>Wraps a client so range requests are answered with the full body, like a misbehaving server
    /// would via HttpNetworkClient (which reports that as <see cref="Core.Interfaces.RangeNotSupportedException"/>).</summary>
    private sealed class RangeIgnoringNetworkClient(FakeNetworkClient inner) : Core.Interfaces.INetworkClient
    {
        public Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken ct = default) => inner.GetFileInfoAsync(url, ct);

        public Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken ct = default)
        {
            if ((from ?? 0) != 0 || (to.HasValue && to != inner.Data.Length - 1))
            {
                throw new Core.Interfaces.RangeNotSupportedException(url);
            }
            return inner.GetStreamAsync(url, from, to, ct);
        }
    }
}
