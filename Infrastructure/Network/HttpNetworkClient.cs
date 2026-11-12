using System.Net;
using System.Net.Http.Headers;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Infrastructure.Network;

public class HttpNetworkClient : INetworkClient
{
    private static readonly ProductInfoHeaderValue UserAgent = new("DotNetDownloadManager", "1.0");

    private readonly HttpClient _httpClient;

    public HttpNetworkClient() : this(CreateDefaultClient())
    {
    }

    /// <summary>Use a caller-supplied client (e.g. with a custom handler for tests or proxies).</summary>
    public HttpNetworkClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        // Recycle connections so DNS changes are picked up during long sessions.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    })
    {
        // Covers sending the request and receiving headers only; body reads are bounded by cancellation.
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        long? size = null;

        // 1. HEAD is cheapest. Many servers answer it fully.
        using (var head = CreateRequest(HttpMethod.Head, url))
        using (var response = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                size = response.Content.Headers.ContentLength;
                if (size.HasValue && response.Headers.AcceptRanges.Contains("bytes"))
                {
                    return new RemoteFileInfo(size, SupportsRanges: true);
                }
            }
        }

        // 2. HEAD failed, lacked a size, or didn't advertise ranges: probe with a 1-byte ranged GET.
        using var probe = CreateRequest(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        using var probeResponse = await _httpClient.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (probeResponse.StatusCode == HttpStatusCode.PartialContent &&
            probeResponse.Content.Headers.ContentRange?.Length is long total)
        {
            return new RemoteFileInfo(total, SupportsRanges: true);
        }

        probeResponse.EnsureSuccessStatusCode();
        return new RemoteFileInfo(probeResponse.Content.Headers.ContentLength ?? size, SupportsRanges: false);
    }

    public async Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(HttpMethod.Get, url);
        var rangeRequested = from.HasValue || to.HasValue;
        if (rangeRequested)
        {
            request.Headers.Range = new RangeHeaderValue(from, to);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // A 200 to a ranged request means the server sent the WHOLE file; writing it at the
            // segment's offset would corrupt the download.
            if (rangeRequested && response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new RangeNotSupportedException(url);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new ResponseStream(body, response, request);
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(UserAgent);
        return request;
    }

    /// <summary>Body stream that also disposes the response and request that own it.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
