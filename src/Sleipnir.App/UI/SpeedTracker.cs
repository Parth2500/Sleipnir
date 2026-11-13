using Sleipnir.Core.Models;

namespace Sleipnir.UI;

/// <summary>
/// Measures transfer speed per download over a sliding time window. Call <see cref="Update"/> on
/// every UI tick; speed is (bytes gained) / (time elapsed) across the samples still in the window.
/// </summary>
public sealed class SpeedTracker
{
    private readonly TimeSpan _window;
    private readonly Dictionary<Guid, Queue<(DateTime Time, long Bytes)>> _samples = new();
    private readonly Dictionary<Guid, double> _speeds = new();

    public SpeedTracker(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(5);

    public void Update(IEnumerable<DownloadTask> downloads, DateTime now)
    {
        var seen = new HashSet<Guid>();
        foreach (var download in downloads)
        {
            seen.Add(download.Id);
            if (download.Status != DownloadStatus.Downloading)
            {
                _samples.Remove(download.Id);
                _speeds.Remove(download.Id);
                continue;
            }

            if (!_samples.TryGetValue(download.Id, out var queue))
            {
                _samples[download.Id] = queue = new Queue<(DateTime, long)>();
            }

            var bytes = download.BytesDownloaded;
            if (queue.Count > 0 && bytes < queue.Last().Bytes)
            {
                queue.Clear(); // the download restarted from an earlier point
            }

            queue.Enqueue((now, bytes));
            while (queue.Count > 1 && now - queue.Peek().Time > _window)
            {
                queue.Dequeue();
            }

            var first = queue.Peek();
            var elapsed = (now - first.Time).TotalSeconds;
            _speeds[download.Id] = queue.Count > 1 && elapsed > 0 ? (bytes - first.Bytes) / elapsed : 0;
        }

        foreach (var gone in _samples.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _samples.Remove(gone);
            _speeds.Remove(gone);
        }
    }

    public double GetBytesPerSecond(Guid downloadId) => _speeds.TryGetValue(downloadId, out var speed) ? speed : 0;

    /// <summary>Estimated time left, or null if the size is unknown or nothing is arriving.</summary>
    public TimeSpan? GetEta(DownloadTask task)
    {
        var speed = GetBytesPerSecond(task.Id);
        if (!task.IsSizeKnown || task.TotalBytes <= 0 || speed <= 0) return null;
        var remaining = Math.Max(0, task.TotalBytes - task.BytesDownloaded);
        return TimeSpan.FromSeconds(remaining / speed);
    }

    public double TotalBytesPerSecond => _speeds.Values.Sum();
}
