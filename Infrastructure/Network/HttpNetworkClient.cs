using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Infrastructure.Network;

public class HttpNetworkClient : INetworkClient
{
    private static readonly ProductInfoHeaderValue UserAgent = new("DotNetDownloadManager", "1.0");

    private readonly HttpClient _httpClient;

    /// <param name="headersTimeout">Maximum time to wait for response headers (default 60 s).</param>
    public HttpNetworkClient(TimeSpan? headersTimeout = null) : this(CreateDefaultClient(headersTimeout ?? TimeSpan.FromSeconds(60)))
    {
    }

    /// <summary>Use a caller-supplied client (e.g. with a custom handler for tests or proxies).</summary>
    public HttpNetworkClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static HttpClient CreateDefaultClient(TimeSpan headersTimeout) => new(new SocketsHttpHandler
    {
        // Recycle connections so DNS changes are picked up during long sessions.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Race IPv6/IPv4 so a broken IPv6 route can't stall every connection (see HappyEyeballs).
        ConnectCallback = async (context, ct) =>
        {
            var socket = await HappyEyeballs.ConnectSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        // Covers sending the request and receiving headers only; body reads are bounded by cancellation.
        Timeout = headersTimeout,
    };

    public Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default) =>
        WithTimeoutsReported(url, cancellationToken, () => ProbeAsync(url, cancellationToken));

    public Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default) =>
        WithTimeoutsReported(url, cancellationToken, () => OpenStreamAsync(url, from, to, cancellationToken));

    /// <summary>
    /// HttpClient reports its own timeouts (connect/headers) as <see cref="TaskCanceledException"/>. Turn those
    /// into <see cref="TimeoutException"/> so they read sensibly and are retried rather than mistaken for a pause.
    /// </summary>
    private static async Task<T> WithTimeoutsReported<T>(string url, CancellationToken cancellationToken, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            throw new TimeoutException($"Timed out connecting to {host} or waiting for its response.", ex);
        }
    }

    private async Task<RemoteFileInfo> ProbeAsync(string url, CancellationToken cancellationToken)
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

        ThrowIfBusy(probeResponse, url);
        if (probeResponse.StatusCode == HttpStatusCode.PartialContent &&
            probeResponse.Content.Headers.ContentRange?.Length is long total)
        {
            return new RemoteFileInfo(total, SupportsRanges: true);
        }

        probeResponse.EnsureSuccessStatusCode();
        return new RemoteFileInfo(probeResponse.Content.Headers.ContentLength ?? size, SupportsRanges: false);
    }

    private async Task<Stream> OpenStreamAsync(string url, long? from, long? to, CancellationToken cancellationToken)
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
            ThrowIfBusy(response, url);
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

    /// <summary>429 / 503 mean "come back later", not "this failed": report them with the server's Retry-After.</summary>
    private static void ThrowIfBusy(HttpResponseMessage response, string url)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return;

        var retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => (TimeSpan?)null,
        };
        throw new ServerBusyException(url, (int)response.StatusCode, retryAfter);
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
