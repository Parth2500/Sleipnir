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
