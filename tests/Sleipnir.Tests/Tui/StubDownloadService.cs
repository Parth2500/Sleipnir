using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;

namespace Sleipnir.Tests.Tui;

/// <summary>An in-memory IDownloadService that records what the UI asked for.</summary>
public sealed class StubDownloadService : IDownloadService
{
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public List<DownloadTask> Tasks { get; } = new();
    public List<string> Calls { get; } = new();

    public DownloadTask Seed(string name, DownloadStatus status, long total = 1000, long done = 0)
    {
        var task = new DownloadTask
        {
            Url = "http://example.test/" + name,
            Filename = name,
            DestinationPath = "/downloads/" + name,
            TotalBytes = total,
            Status = status,
            CreatedAt = _clock = _clock.AddMinutes(1),
            Segments = new[] { new DownloadSegment { StartOffset = 0, EndOffset = total - 1, BytesDownloaded = done } },
        };
        Tasks.Add(task);
        return task;
    }

    public Task<Guid> AddDownloadAsync(string url, string outputDirectory)
    {
        Calls.Add($"add {url} -> {outputDirectory}");
        var task = Seed(new Uri(url).Segments.Last(), DownloadStatus.Queued);
        task.Url = url;
        return Task.FromResult(task.Id);
    }

    public Task PauseDownloadAsync(Guid id) => Act("pause", id, DownloadStatus.Paused);
    public Task ResumeDownloadAsync(Guid id) => Act("resume", id, DownloadStatus.Downloading);
    public Task CancelDownloadAsync(Guid id) => Act("cancel", id, DownloadStatus.Cancelled);

    public Task RemoveDownloadAsync(Guid id)
    {
        Calls.Add($"remove {Name(id)}");
        // Like the real engine: only finished downloads leave the list.
        Tasks.RemoveAll(t => t.Id == id && t.Status is DownloadStatus.Completed or DownloadStatus.Cancelled);
        return Task.CompletedTask;
    }

    public IEnumerable<DownloadTask> GetAllDownloads() => Tasks;
    public DownloadTask? GetDownload(Guid id) => Tasks.FirstOrDefault(t => t.Id == id);
    public Task RestoreAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;

    private string Name(Guid id) => GetDownload(id)?.Filename ?? "?";

    private Task Act(string verb, Guid id, DownloadStatus status)
    {
        Calls.Add($"{verb} {Name(id)}");
        if (GetDownload(id) is { } task) task.Status = status;
        return Task.CompletedTask;
    }
}
