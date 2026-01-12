using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using DotNetDownloadManager.Core.Interfaces;

namespace DotNetDownloadManager.Infrastructure.Network;

public class HttpNetworkClient : INetworkClient
{
    private readonly HttpClient _httpClient;

    public HttpNetworkClient()
    {
        _httpClient = new HttpClient();
    }

    public async Task<long> GetFileSizeAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength.HasValue)
        {
            return response.Content.Headers.ContentLength.Value;
        }

        // Fallback: If HEAD doesn't return content length, try GET with range 0-0 or just full GET headers (riskier but sometimes necessary)
        // For now, strict mode:
        throw new Exception("Could not determine file size from server headers.");
    }

    public async Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (from.HasValue || to.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(from, to);
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync();
    }
}
