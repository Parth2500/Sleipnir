using Sleipnir.Core.Models;
using Sleipnir.UI;

namespace Sleipnir.Tests;

public class SpeedTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DownloadTask Downloading(long total, long done)
    {
        var task = new DownloadTask { TotalBytes = total, Status = DownloadStatus.Downloading };
        task.Segments = new[] { new DownloadSegment { StartOffset = 0, EndOffset = total - 1, BytesDownloaded = done } };
        return task;
    }

    private static void SetBytes(DownloadTask task, long bytes) => task.Segments[0].BytesDownloaded = bytes;

    [Fact]
    public void Speed_is_bytes_gained_over_the_sampling_window()
    {
        var tracker = new SpeedTracker(TimeSpan.FromSeconds(5));
        var task = Downloading(10_000, 0);

        tracker.Update(new[] { task }, T0);
        SetBytes(task, 1_000);
        tracker.Update(new[] { task }, T0.AddSeconds(1));
        SetBytes(task, 3_000);
        tracker.Update(new[] { task }, T0.AddSeconds(2));

        Assert.Equal(1_500, tracker.GetBytesPerSecond(task.Id), precision: 0);
        Assert.Equal(1_500, tracker.TotalBytesPerSecond, precision: 0);
    }

    [Fact]
    public void Old_samples_fall_out_of_the_window()
    {
        var tracker = new SpeedTracker(TimeSpan.FromSeconds(2));
        var task = Downloading(1_000_000, 0);

        tracker.Update(new[] { task }, T0);
        SetBytes(task, 100_000);                       // a fast burst...
        tracker.Update(new[] { task }, T0.AddSeconds(1));
        SetBytes(task, 100_100);                       // ...then it slows to 100 B/s
        tracker.Update(new[] { task }, T0.AddSeconds(4));
        SetBytes(task, 100_200);
        tracker.Update(new[] { task }, T0.AddSeconds(5));

        Assert.Equal(100, tracker.GetBytesPerSecond(task.Id), precision: 0);
    }

    [Fact]
    public void Eta_is_remaining_bytes_divided_by_speed()
    {
        var tracker = new SpeedTracker();
        var task = Downloading(10_000, 0);
        tracker.Update(new[] { task }, T0);
        SetBytes(task, 2_000);
        tracker.Update(new[] { task }, T0.AddSeconds(2)); // 1000 B/s, 8000 B left

        Assert.Equal(TimeSpan.FromSeconds(8), tracker.GetEta(task));
    }

    [Fact]
    public void Eta_is_unknown_without_a_size_or_without_progress()
    {
        var tracker = new SpeedTracker();
        var stalled = Downloading(10_000, 500);
        var unknownSize = Downloading(10_000, 0);
        unknownSize.IsSizeKnown = false;

        tracker.Update(new[] { stalled, unknownSize }, T0);
        SetBytes(unknownSize, 1_000);
        tracker.Update(new[] { stalled, unknownSize }, T0.AddSeconds(1));

        Assert.Null(tracker.GetEta(stalled));
        Assert.Null(tracker.GetEta(unknownSize));
    }

    [Fact]
    public void Progress_going_backwards_restarts_measurement_instead_of_reporting_negative_speed()
    {
        var tracker = new SpeedTracker();
        var task = Downloading(10_000, 5_000);
        tracker.Update(new[] { task }, T0);
        SetBytes(task, 0); // single-stream download restarted from scratch
        tracker.Update(new[] { task }, T0.AddSeconds(1));

        Assert.True(tracker.GetBytesPerSecond(task.Id) >= 0);
    }

    [Fact]
    public void Downloads_that_are_not_running_report_zero_speed()
    {
        var tracker = new SpeedTracker();
        var task = Downloading(10_000, 0);
        tracker.Update(new[] { task }, T0);
        SetBytes(task, 5_000);
        task.Status = DownloadStatus.Paused;
        tracker.Update(new[] { task }, T0.AddSeconds(1));

        Assert.Equal(0, tracker.GetBytesPerSecond(task.Id));
        Assert.Equal(0, tracker.TotalBytesPerSecond);
    }
}
