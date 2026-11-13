using System.Net;
using System.Net.Sockets;

namespace Sleipnir.Infrastructure.Network;

/// <summary>
/// Happy Eyeballs (RFC 8305, simplified): connection attempts to a host's addresses are raced, with a
/// short head start for each one, and the first to succeed wins.
/// </summary>
/// <remarks>
/// .NET's default connector tries addresses one by one and can sit on an unreachable address (typically
/// a black-holed IPv6 route) until the connect timeout fires, never trying the working IPv4 address.
/// Browsers and curl avoid this with Happy Eyeballs; so do we.
/// </remarks>
public static class HappyEyeballs
{
    /// <summary>Delay before starting the next attempt while earlier ones are still pending (RFC 8305 suggests 250 ms).</summary>
    public static readonly TimeSpan DefaultAttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Orders addresses so the families alternate (e.g. v6, v4, v6, v4), keeping the resolver's
    /// preferred family first and the original order within each family.
    /// </summary>
    public static IReadOnlyList<IPAddress> Interleave(IEnumerable<IPAddress> addresses)
    {
        var list = addresses.ToList();
        if (list.Count == 0) return list;

        var firstFamily = list[0].AddressFamily;
        var primary = new Queue<IPAddress>(list.Where(a => a.AddressFamily == firstFamily));
        var secondary = new Queue<IPAddress>(list.Where(a => a.AddressFamily != firstFamily));

        var result = new List<IPAddress>(list.Count);
        while (primary.Count > 0 || secondary.Count > 0)
        {
            if (primary.Count > 0) result.Add(primary.Dequeue());
            if (secondary.Count > 0) result.Add(secondary.Dequeue());
        }
        return result;
    }

    /// <summary>
    /// Races <paramref name="connect"/> over <paramref name="addresses"/> (in the given order).
    /// A new attempt starts when the previous one fails, or after <paramref name="attemptDelay"/> if it
    /// is still pending. Losing attempts are cancelled, and any that still succeed are disposed.
    /// </summary>
    public static async Task<T> ConnectAsync<T>(
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress, CancellationToken, Task<T>> connect,
        TimeSpan attemptDelay,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var timers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Dictionary<Task<T>, CancellationTokenSource>();
        var errors = new List<Exception>();
        var next = 0;
        Task? timer = null;

        void StartNext()
        {
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            running[Invoke(connect, addresses[next++], attemptCts.Token)] = attemptCts;
            timer = next < addresses.Count ? Task.Delay(attemptDelay, timers.Token) : null;
        }

        try
        {
            StartNext();
            while (running.Count > 0)
            {
                var waitOn = new List<Task>(running.Keys);
                if (timer is not null) waitOn.Add(timer);

                var finished = await Task.WhenAny(waitOn);
                cancellationToken.ThrowIfCancellationRequested();

                if (finished == timer)
                {
                    StartNext(); // head start is over: race the next address too
                    continue;
                }

                var attempt = (Task<T>)finished;
                running.Remove(attempt, out var attemptCts);
                attemptCts?.Dispose();

                if (attempt.IsCompletedSuccessfully)
                {
                    var winner = attempt.Result;
                    foreach (var (loser, loserCts) in running)
                    {
                        loserCts.Cancel();
                        _ = loser.ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully) t.Result.Dispose();
                            loserCts.Dispose();
                        }, TaskScheduler.Default);
                    }
                    running.Clear();
                    return winner;
                }

                errors.Add(attempt.Exception?.InnerException ?? new OperationCanceledException());
                if (next < addresses.Count) StartNext(); // failed fast: don't wait for the head start
            }

            throw errors.Count == 1
                ? errors[0]
                : new AggregateException($"All {errors.Count} connection attempts failed.", errors);
        }
        finally
        {
            timers.Cancel();
            foreach (var (pending, pendingCts) in running) // only non-empty if we are exiting via an exception
            {
                pendingCts.Cancel();
                _ = pending.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    pendingCts.Dispose();
                }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Connects a TCP socket to <paramref name="host"/>:<paramref name="port"/> using Happy Eyeballs.</summary>
    public static async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        return await ConnectAsync(Interleave(addresses), async (address, ct) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }, DefaultAttemptDelay, cancellationToken);
    }

    private static Task<T> Invoke<T>(Func<IPAddress, CancellationToken, Task<T>> connect, IPAddress address, CancellationToken ct)
    {
        try { return connect(address, ct); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }
}
