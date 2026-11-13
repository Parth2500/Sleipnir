using System.Security.Cryptography;
using System.Text;

namespace Sleipnir.Server.Api;

/// <summary>Remembers the answer to a request carrying an <c>Idempotency-Key</c>, so a retry doesn't repeat the work.</summary>
public sealed class IdempotencyCache<T>(int capacity = 1000, TimeSpan? lifetime = null, Func<DateTimeOffset>? clock = null)
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromHours(1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (string BodyHash, T Result, DateTimeOffset At)> _entries = new();
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public enum Lookup { Miss, Hit, KeyReused }

    public Lookup TryGet(string key, string bodyHash, out T? result)
    {
        lock (_gate)
        {
            result = default;
            if (!_entries.TryGetValue(key, out var entry) || _clock() - entry.At > _lifetime) return Lookup.Miss;
            if (entry.BodyHash != bodyHash) return Lookup.KeyReused;
            result = entry.Result;
            return Lookup.Hit;
        }
    }

    public void Store(string key, string bodyHash, T result)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key)) _order.Enqueue(key);
            _entries[key] = (bodyHash, result, _clock());
            while (_order.Count > capacity) _entries.Remove(_order.Dequeue());
        }
    }
}
