using System.Net;
using System.Net.Sockets;
using Sleipnir.Core.Models;
using Sleipnir.Infrastructure.Network;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;

namespace Sleipnir.Tests;

/// <summary>Real HTTP over loopback: HttpNetworkClient + DownloadService + LocalFileStorage.</summary>
public class EndToEndTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Downloads_over_real_http_match_the_source(bool serverSupportsRanges)
    {
        var data = TestHelpers.RandomBytes(3 * 1024 * 1024 + 123);
        using var server = new LoopbackFileServer(data, serverSupportsRanges);
        using var dir = new TempDir();
        var service = new DownloadService(new HttpNetworkClient(), new LocalFileStorage(),
            new DownloadOptions { SegmentCount = 4, MinSegmentSize = 256 * 1024, RetryDelay = TimeSpan.Zero });

        var id = await service.AddDownloadAsync(server.Url + "big%20file.bin", dir.Path);
        Assert.True(await TestHelpers.WaitUntil(
            () => service.GetDownload(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed, 15000));

        var task = service.GetDownload(id)!;
        Assert.True(task.Status == DownloadStatus.Completed, task.ErrorMessage);
        Assert.Equal("big file.bin", task.Filename);
        Assert.Equal(serverSupportsRanges ? 4 : 1, task.Segments.Count);
        Assert.Equal(data, await File.ReadAllBytesAsync(task.DestinationPath));
    }

    [Fact]
    public async Task Body_that_takes_longer_than_the_header_timeout_still_downloads()
    {
        // The header timeout must only bound waiting for response headers, never the body transfer:
        // a large segment on a slow link can take many minutes.
        var data = TestHelpers.RandomBytes(200_000);
        using var server = new LoopbackFileServer(data, ranges: true, throttle: TimeSpan.FromMilliseconds(100));
        using var dir = new TempDir();
        var service = new DownloadService(new HttpNetworkClient(headersTimeout: TimeSpan.FromSeconds(1)), new LocalFileStorage(),
            new DownloadOptions { SegmentCount = 1, MaxRetries = 0 });

        var id = await service.AddDownloadAsync(server.Url + "slow.bin", dir.Path); // ~2.5 s to transfer
        Assert.True(await TestHelpers.WaitUntil(
            () => service.GetDownload(id)!.Status is DownloadStatus.Completed or DownloadStatus.Failed, 15000));

        var task = service.GetDownload(id)!;
        Assert.True(task.Status == DownloadStatus.Completed, task.ErrorMessage);
        Assert.Equal(data, await File.ReadAllBytesAsync(task.DestinationPath));
    }

    private sealed class LoopbackFileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _data;
        private readonly bool _ranges;
        private readonly TimeSpan _throttle;

        public string Url { get; }

        /// <param name="throttle">If set, the body is sent in 8 KB chunks with this pause between them.</param>
        public LoopbackFileServer(byte[] data, bool ranges, TimeSpan throttle = default)
        {
            _data = data;
            _ranges = ranges;
            _throttle = throttle;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            try
            {
                long start = 0, end = _data.Length - 1;
                var range = ctx.Request.Headers["Range"];
                if (_ranges && range is not null && range.StartsWith("bytes="))
                {
                    var parts = range[6..].Split('-');
                    start = long.Parse(parts[0]);
                    if (parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), end);
                    res.StatusCode = 206;
                    res.Headers["Content-Range"] = $"bytes {start}-{end}/{_data.Length}";
                }
                if (_ranges) res.Headers["Accept-Ranges"] = "bytes";

                res.ContentLength64 = end - start + 1;
                if (ctx.Request.HttpMethod != "HEAD")
                {
                    if (_throttle == TimeSpan.Zero)
                    {
                        await res.OutputStream.WriteAsync(_data.AsMemory((int)start, (int)(end - start + 1)));
                    }
                    else
                    {
                        for (var pos = start; pos <= end; pos += 8192)
                        {
                            await res.OutputStream.WriteAsync(_data.AsMemory((int)pos, (int)Math.Min(8192, end - pos + 1)));
                            await res.OutputStream.FlushAsync();
                            await Task.Delay(_throttle);
                        }
                    }
                }
            }
            catch { /* client went away */ }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
