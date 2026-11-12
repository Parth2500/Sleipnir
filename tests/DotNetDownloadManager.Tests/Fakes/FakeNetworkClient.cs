using System.Collections.Concurrent;
using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Tests.Fakes;

/// <summary>
/// In-memory "server" that serves a byte array. Behaviour can be customised per request
/// to simulate stalls, failures, and servers that ignore Range headers.
/// </summary>
public sealed class FakeNetworkClient : INetworkClient
{
    private readonly byte[] _data;
    private int _openStreams;
    private int _requestCount;

    public FakeNetworkClient(byte[] data) => _data = data;

    /// <summary>Reported by <see cref="GetFileInfoAsync"/>.</summary>
    public bool SupportsRanges { get; init; } = true;

    /// <summary>When false, Range headers are ignored and the whole file is returned (like a 200 response).</summary>
    public bool HonorRanges { get; init; } = true;

    /// <summary>
    /// Optional hook run before each stream is served. Receives the request number (0-based) and the
    /// requested start offset. Return a stream to serve instead, throw to fail, or return null for the default.
    /// </summary>
    public Func<int, long?, CancellationToken, Stream?>? OnRequest { get; init; }

    public int OpenStreams => Volatile.Read(ref _openStreams);
    public int RequestCount => Volatile.Read(ref _requestCount);
    public ConcurrentQueue<(long? From, long? To)> Requests { get; } = new();

    public byte[] Data => _data;

    public Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteFileInfo(_data.Length, SupportsRanges));

    public Task<Stream> GetStreamAsync(string url, long? from = null, long? to = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = Interlocked.Increment(ref _requestCount) - 1;
        Requests.Enqueue((from, to));

        var custom = OnRequest?.Invoke(n, from, cancellationToken);
        Stream inner;
        if (custom != null)
        {
            inner = custom;
        }
        else if (HonorRanges && (from.HasValue || to.HasValue))
        {
            var start = (int)(from ?? 0);
            var end = (int)(to ?? _data.Length - 1);
            inner = new MemoryStream(_data, start, end - start + 1, writable: false);
        }
        else
        {
            inner = new MemoryStream(_data, writable: false);
        }

        Interlocked.Increment(ref _openStreams);
        return Task.FromResult<Stream>(new TrackingStream(inner, () => Interlocked.Decrement(ref _openStreams)));
    }

    /// <summary>A stream that serves <paramref name="bytesBeforeStall"/> bytes of data starting at <paramref name="from"/>, then blocks until cancelled.</summary>
    public Stream Stalling(long? from, int bytesBeforeStall)
    {
        var start = (int)(from ?? 0);
        var count = Math.Min(bytesBeforeStall, _data.Length - start);
        return new StallingStream(new MemoryStream(_data, start, count, writable: false));
    }

    private sealed class TrackingStream(Stream inner, Action onDispose) : Stream
    {
        private int _disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                onDispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StallingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            if (n > 0) return n;
            await Task.Delay(Timeout.Infinite, ct); // stall until cancelled
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { inner.Dispose(); base.Dispose(disposing); }
    }
}
