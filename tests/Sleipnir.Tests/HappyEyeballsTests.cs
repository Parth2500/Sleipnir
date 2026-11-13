using System.Diagnostics;
using System.Net;
using Sleipnir.Infrastructure.Network;

namespace Sleipnir.Tests;

/// <summary>
/// Happy Eyeballs (RFC 8305): race connection attempts so one unreachable address family
/// (typically a black-holed IPv6 route) cannot stall the connection.
/// </summary>
public class HappyEyeballsTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4a = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    private sealed class FakeConnection(IPAddress address) : IDisposable
    {
        public IPAddress Address { get; } = address;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Address_families_are_interleaved_keeping_the_first_family_first()
    {
        var ordered = HappyEyeballs.Interleave(new[] { V6a, V6b, V4a, V4b });

        Assert.Equal(new[] { V6a, V4a, V6b, V4b }, ordered);
    }

    [Fact]
    public async Task Address_that_hangs_does_not_block_the_next_one()
    {
        var sw = Stopwatch.StartNew();

        var winner = await HappyEyeballs.ConnectAsync(new[] { V6a, V4a }, async (address, ct) =>
        {
            if (address.Equals(V6a)) await Task.Delay(Timeout.Infinite, ct); // black hole
            return new FakeConnection(address);
        }, Delay, CancellationToken.None);

        Assert.Equal(V4a, winner.Address);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Address_that_fails_fast_starts_the_next_attempt_immediately()
    {
        var started = new List<(IPAddress, TimeSpan)>();
        var sw = Stopwatch.StartNew();

        var winner = await HappyEyeballs.ConnectAsync(new[] { V6a, V4a }, (address, _) =>
        {
            lock (started) started.Add((address, sw.Elapsed));
            return address.Equals(V6a)
                ? Task.FromException<FakeConnection>(new IOException("network unreachable"))
                : Task.FromResult(new FakeConnection(address));
        }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(V4a, winner.Address);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "waited for the attempt delay after a failure");
    }

    [Fact]
    public async Task When_every_address_fails_the_error_is_reported()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => HappyEyeballs.ConnectAsync(new[] { V6a, V4a },
            (address, _) => Task.FromException<FakeConnection>(new IOException($"refused {address}")),
            Delay, CancellationToken.None));

        Assert.Contains("refused", ex.ToString());
    }

    [Fact]
    public async Task Losing_connections_are_cancelled_or_disposed()
    {
        var loserCancelled = new TaskCompletionSource();

        var winner = await HappyEyeballs.ConnectAsync(new[] { V6a, V4a }, async (address, ct) =>
        {
            if (address.Equals(V6a))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    loserCancelled.TrySetResult();
                    throw;
                }
            }
            return new FakeConnection(address);
        }, Delay, CancellationToken.None);

        Assert.Equal(V4a, winner.Address);
        await loserCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Caller_cancellation_stops_all_attempts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HappyEyeballs.ConnectAsync(new[] { V6a, V4a },
            async (address, ct) => { await Task.Delay(Timeout.Infinite, ct); return new FakeConnection(address); },
            Delay, cts.Token));
    }
}
