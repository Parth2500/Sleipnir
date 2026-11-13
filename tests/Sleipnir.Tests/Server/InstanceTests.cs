using Sleipnir.Server.Hosting;

namespace Sleipnir.Tests.Server;

public class InstanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleipnir-instance-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Only_one_holder_at_a_time_and_the_lock_is_released_on_dispose()
    {
        var first = InstanceLock.TryAcquire(_dir);
        Assert.NotNull(first);

        Assert.Null(InstanceLock.TryAcquire(_dir));

        first!.Dispose();
        using var third = InstanceLock.TryAcquire(_dir);
        Assert.NotNull(third);
    }

    [Fact]
    public void A_lock_file_left_behind_by_a_crashed_process_does_not_block()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "daemon.lock"), "left over");

        using var acquired = InstanceLock.TryAcquire(_dir);

        Assert.NotNull(acquired);
    }

    [Fact]
    public void DaemonInfo_round_trips_and_overwrites()
    {
        DaemonInfo.Write(_dir, new DaemonInfo(7766, 100, "1.0.0"));
        DaemonInfo.Write(_dir, new DaemonInfo(7767, 200, "1.0.1"));

        Assert.Equal(new DaemonInfo(7767, 200, "1.0.1"), DaemonInfo.TryRead(_dir));
    }

    [Fact]
    public void TryRead_returns_null_for_missing_or_corrupt_files()
    {
        Assert.Null(DaemonInfo.TryRead(_dir));

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "daemon.json"), "{ not json");
        Assert.Null(DaemonInfo.TryRead(_dir));
    }

    [Fact]
    public void Delete_removes_the_file_and_tolerates_a_missing_one()
    {
        DaemonInfo.Write(_dir, new DaemonInfo(1, 2, "3"));

        DaemonInfo.Delete(_dir);
        DaemonInfo.Delete(_dir);

        Assert.Null(DaemonInfo.TryRead(_dir));
    }
}
