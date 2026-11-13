namespace Sleipnir.Core.Interfaces;

/// <summary>
/// The server asked us to slow down (HTTP 429 Too Many Requests or 503 Service Unavailable).
/// Worth retrying later; <see cref="RetryAfter"/> is the server's requested wait, if it sent one.
/// </summary>
public sealed class ServerBusyException(string url, int statusCode, TimeSpan? retryAfter)
    : Exception($"Server is busy (HTTP {statusCode}){(retryAfter is { } wait ? $"; asked to retry after {wait.TotalSeconds:0} s" : "")}: {url}")
{
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
