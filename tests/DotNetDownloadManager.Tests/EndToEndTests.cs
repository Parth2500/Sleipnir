using System.Net;
using System.Net.Sockets;
using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.Infrastructure.Network;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;

namespace DotNetDownloadManager.Tests;

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

    private sealed class LoopbackFileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _data;
        private readonly bool _ranges;

        public string Url { get; }

        public LoopbackFileServer(byte[] data, bool ranges)
        {
            _data = data;
            _ranges = ranges;
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
                    await res.OutputStream.WriteAsync(_data.AsMemory((int)start, (int)(end - start + 1)));
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
