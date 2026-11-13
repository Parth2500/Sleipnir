namespace Sleipnir.Server.Security;

/// <summary>Counts failed sign-ins per source and blocks a source that fails too often within a window.</summary>
public sealed class AuthFailureLimiter(int maxFailures = 20, TimeSpan? window = null, Func<DateTimeOffset>? clock = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new();
    private readonly object _gate = new();

    public bool IsBlocked(string source)
    {
        lock (_gate)
        {
            return Prune(source)?.Count >= maxFailures;
        }
    }

    public void RecordFailure(string source)
    {
        lock (_gate)
        {
            var queue = Prune(source);
            if (queue is null) _failures[source] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(_clock());
        }
    }

    private Queue<DateTimeOffset>? Prune(string source)
    {
        if (!_failures.TryGetValue(source, out var queue)) return null;
        var cutoff = _clock() - _window;
        while (queue.Count > 0 && queue.Peek() <= cutoff) queue.Dequeue();
        if (queue.Count == 0)
        {
            _failures.Remove(source);
            return null;
        }
        return queue;
    }
}
