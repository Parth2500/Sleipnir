namespace DotNetDownloadManager.Core.Interfaces;

/// <summary>
/// Thrown by <see cref="INetworkClient.GetStreamAsync"/> when a byte range was requested
/// but the server answered with the whole file instead of a 206 Partial Content response.
/// </summary>
public sealed class RangeNotSupportedException(string url)
    : Exception($"The server ignored the HTTP Range request for {url}.")
{
    public string Url { get; } = url;
}
