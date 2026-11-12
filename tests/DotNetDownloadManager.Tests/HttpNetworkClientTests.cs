using System.Net;
using System.Net.Http.Headers;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Infrastructure.Network;

namespace DotNetDownloadManager.Tests;

public class HttpNetworkClientTests
{
    private const string Url = "http://example.test/file.bin";

    private static HttpNetworkClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private static HttpResponseMessage Body(HttpStatusCode status, int length, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[length]) };
        configure?.Invoke(response);
        return response;
    }

    [Fact]
    public async Task Range_request_answered_with_200_is_rejected_instead_of_returning_the_wrong_bytes()
    {
        var client = Client(_ => Body(HttpStatusCode.OK, 1000));

        await Assert.ThrowsAsync<RangeNotSupportedException>(() => client.GetStreamAsync(Url, 500, 999));
    }

    [Fact]
    public async Task Range_request_answered_with_206_returns_the_stream()
    {
        var client = Client(_ => Body(HttpStatusCode.PartialContent, 500));

        await using var stream = await client.GetStreamAsync(Url, 500, 999);

        Assert.Equal(500, await ReadAll(stream));
    }

    [Fact]
    public async Task Head_with_accept_ranges_reports_size_and_range_support()
    {
        var client = Client(req => Body(HttpStatusCode.OK, 1234, r => r.Headers.AcceptRanges.Add("bytes")));

        var info = await client.GetFileInfoAsync(Url);

        Assert.Equal(1234, info.Size);
        Assert.True(info.SupportsRanges);
    }

    [Fact]
    public async Task Server_that_answers_range_probe_with_200_is_reported_as_not_supporting_ranges()
    {
        var client = Client(_ => Body(HttpStatusCode.OK, 1234));

        var info = await client.GetFileInfoAsync(Url);

        Assert.Equal(1234, info.Size);
        Assert.False(info.SupportsRanges);
    }

    [Fact]
    public async Task Server_that_rejects_head_is_probed_with_a_ranged_get()
    {
        var client = Client(req => req.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : Body(HttpStatusCode.PartialContent, 1, r => r.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 5555)));

        var info = await client.GetFileInfoAsync(Url);

        Assert.Equal(5555, info.Size);
        Assert.True(info.SupportsRanges);
    }

    [Fact]
    public async Task Server_that_never_reports_a_size_yields_unknown_size()
    {
        // StreamContent over a non-seekable stream has no Content-Length, like a chunked response.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream()) });

        var info = await client.GetFileInfoAsync(Url);

        Assert.Null(info.Size);
        Assert.False(info.SupportsRanges);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_timeout_not_as_a_cancellation()
    {
        // HttpClient/SocketsHttpHandler surface timeouts as TaskCanceledException ("The operation was canceled."),
        // which is meaningless to a user and is mistaken for a pause by cancellation-aware code.
        var client = new HttpNetworkClient(new HttpClient(new ThrowingHandler(
            () => new TaskCanceledException("The operation was canceled.", new TimeoutException()))));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.GetStreamAsync(Url));
        Assert.Contains("example.test", ex.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetFileInfoAsync(Url));
    }

    [Fact]
    public async Task Real_cancellation_is_still_reported_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new HttpNetworkClient(new HttpClient(new ThrowingHandler(() => new TaskCanceledException())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStreamAsync(Url, cancellationToken: cts.Token));
    }

    private sealed class ThrowingHandler(Func<Exception> error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(error());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Busy_server_is_reported_with_its_retry_after(int status)
    {
        var client = Client(_ => Body((HttpStatusCode)status, 0, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))));

        var ex = await Assert.ThrowsAsync<ServerBusyException>(() => client.GetStreamAsync(Url, 0, 99));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task Client_errors_keep_their_status_code()
    {
        var client = Client(_ => Body(HttpStatusCode.NotFound, 0));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStreamAsync(Url));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Requests_carry_a_user_agent()
    {
        HttpRequestMessage? seen = null;
        var client = Client(req => { seen = req; return Body(HttpStatusCode.OK, 10); });

        await using var _ = await client.GetStreamAsync(Url);

        Assert.NotNull(seen);
        Assert.NotEmpty(seen!.Headers.UserAgent);
    }

    private static async Task<int> ReadAll(Stream s)
    {
        var total = 0;
        var buf = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0) total += n;
        return total;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
