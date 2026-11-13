namespace Sleipnir.Core.Models;

/// <summary>
/// What the server told us about a remote file before downloading it.
/// </summary>
/// <param name="Size">Total size in bytes, or null if the server did not report it.</param>
/// <param name="SupportsRanges">True if the server honours HTTP Range requests (needed for segmented downloads and resume).</param>
public sealed record RemoteFileInfo(long? Size, bool SupportsRanges);
