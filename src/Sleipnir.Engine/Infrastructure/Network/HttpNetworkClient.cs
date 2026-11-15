using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;

namespace Sleipnir.Infrastructure.Network;

public class HttpNetworkClient : INetworkClient
{
    private static readonly ProductInfoHeaderValue UserAgent = new("Sleipnir", "1.0");

    private readonly HttpClient _httpClient;
    private readonly HttpClient? _publicOnlyClient;

    /// <param name="headersTimeout">Maximum time to wait for response headers (default 60 s).</param>
    public HttpNetworkClient(TimeSpan? headersTimeout = null)
        : this(CreateDefaultClient(headersTimeout ?? TimeSpan.FromSeconds(60), publicOnly: false),
               CreateDefaultClient(headersTimeout ?? TimeSpan.FromSeconds(60), publicOnly: true))
    {
    }

    /// <summary>Use caller-supplied clients (e.g. with a custom handler for tests or proxies).</summary>
    /// <param name="publicOnlyClient">
    /// The client for public-only requests. Without one, such requests are refused: the guard lives in
    /// the connect step, which a custom handler bypasses.
    /// </param>
    public HttpNetworkClient(HttpClient httpClient, HttpClient? publicOnlyClient = null)
    {
        _httpClient = httpClient;
        _publicOnlyClient = publicOnlyClient;
    }

    /// <summary>
    /// Public-only requests get their own client, so they never reuse a pooled connection that an
    /// unrestricted request opened to a home-network address.
    /// </summary>
    private static HttpClient CreateDefaultClient(TimeSpan headersTimeout, bool publicOnly) => new(new SocketsHttpHandler
    {
        // Recycle connections so DNS changes are picked up during long sessions.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Race IPv6/IPv4 so a broken IPv6 route can't stall every connection (see HappyEyeballs).
        ConnectCallback = async (context, ct) =>
        {
            // The check runs on the addresses actually dialled, for every connection, so it covers
            // redirects and DNS rebinding too (spec §9).
            var socket = await HappyEyeballs.ConnectSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct,
                publicOnly ? PublicAddress.IsPublic : null);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        // Covers sending the request and receiving headers only; body reads are bounded by cancellation.
        Timeout = headersTimeout,
    };

    public Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default, bool publicOnly = false) =>
        WithTimeoutsReported(url, cancellationToken, () => ProbeAsync(ClientFor(publicOnly), url, cancellationToken));

    public Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default, bool publicOnly = false) =>
        WithTimeoutsReported(url, cancellationToken, () => OpenStreamAsync(ClientFor(publicOnly), url, from, to, cancellationToken));

    private HttpClient ClientFor(bool publicOnly) => !publicOnly ? _httpClient
        : _publicOnlyClient ?? throw new InvalidOperationException("This network client has no guarded client for public-only requests.");

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
        catch (HttpRequestException ex) when (ex.InnerException is DestinationNotPublicException refused)
        {
            throw refused; // HttpClient wraps what the connect step throws
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            throw new TimeoutException($"Timed out connecting to {host} or waiting for its response.", ex);
        }
    }

    private static async Task<RemoteFileInfo> ProbeAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        long? size = null;

        // 1. HEAD is cheapest. Many servers answer it fully.
        using (var head = CreateRequest(HttpMethod.Head, url))
        using (var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
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
        using var probeResponse = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        ThrowIfBusy(probeResponse, url);
        if (probeResponse.StatusCode == HttpStatusCode.PartialContent &&
            probeResponse.Content.Headers.ContentRange?.Length is long total)
        {
            return new RemoteFileInfo(total, SupportsRanges: true);
        }

        probeResponse.EnsureSuccessStatusCode();
        return new RemoteFileInfo(probeResponse.Content.Headers.ContentLength ?? size, SupportsRanges: false);
    }

    private static async Task<Stream> OpenStreamAsync(HttpClient client, string url, long? from, long? to, CancellationToken cancellationToken)
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
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
