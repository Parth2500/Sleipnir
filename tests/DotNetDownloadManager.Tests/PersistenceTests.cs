using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.Infrastructure.Persistence;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.Tests.Fakes;

namespace DotNetDownloadManager.Tests;

public class PersistenceTests
{
    private const string Url = "http://example.test/files/file.bin";

    private static DownloadOptions Options(int flushThreshold = 1_000) => new()
    {
        SegmentCount = 4,
        MinSegmentSize = 1,
        RetryDelay = TimeSpan.Zero,
        FlushThreshold = flushThreshold,
        StateSaveInterval = TimeSpan.FromMilliseconds(50),
    };

    private static FakeNetworkClient StallingNetwork(byte[] data, int bytesBeforeStall)
    {
        FakeNetworkClient? network = null;
        network = new FakeNetworkClient(data) { OnRequest = (_, from, _) => network!.Stalling(from, bytesBeforeStall) };
        return network;
    }

    [Fact]
    public async Task Store_round_trips_records_and_deletes_them()
    {
        using var dir = new TempDir();
        var store = new JsonDownloadStateStore(dir.File("state"));
        var record = new DownloadRecord(Guid.NewGuid(), Url, "/x/file.bin", "file.bin", 100, true, true,
            DownloadStatus.Paused, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null,
            new[] { new SegmentRecord(0, 0, 99, 42, false) });

        await store.SaveAsync(record);
        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(record.Id, loaded.Id);
        Assert.Equal(DownloadStatus.Paused, loaded.Status);
        Assert.Equal(42, Assert.Single(loaded.Segments).BytesDownloaded);

        await store.DeleteAsync(record.Id);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task Corrupt_state_file_is_skipped_instead_of_crashing_start_up()
    {
        using var dir = new TempDir();
        var stateDir = dir.File("state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, Guid.NewGuid() + ".json"), "{ not json");

        Assert.Empty(await new JsonDownloadStateStore(stateDir).LoadAllAsync());
    }

    [Fact]
    public async Task Paused_download_survives_a_restart_and_completes_intact()
    {
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(400_000);
        var store = new JsonDownloadStateStore(dir.File("state"));

        var first = new DownloadService(StallingNetwork(data, 20_000), new LocalFileStorage(), Options(), store);
        var id = await first.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => first.GetDownload(id)!.BytesDownloaded >= 4 * 20_000));
        await first.PauseDownloadAsync(id);
        var bytesBeforeRestart = first.GetDownload(id)!.BytesDownloaded;

        // "Restart": a brand-new service with a well-behaved network and the same state store.
        var second = new DownloadService(new FakeNetworkClient(data), new LocalFileStorage(), Options(), store);
        await second.RestoreAsync();

        var restored = second.GetDownload(id);
        Assert.NotNull(restored);
        Assert.Equal(DownloadStatus.Paused, restored!.Status);
        Assert.Equal(bytesBeforeRestart, restored.BytesDownloaded);

        await second.ResumeDownloadAsync(id);
        Assert.True(await TestHelpers.WaitUntil(() => second.GetDownload(id)!.Status == DownloadStatus.Completed));
        Assert.Equal(data, await File.ReadAllBytesAsync(restored.DestinationPath));
    }

    [Fact]
    public async Task Downloads_interrupted_by_shutdown_resume_automatically_on_next_start()
    {
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(400_000);
        var store = new JsonDownloadStateStore(dir.File("state"));

        var first = new DownloadService(StallingNetwork(data, 20_000), new LocalFileStorage(), Options(), store);
        var id = await first.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => first.GetDownload(id)!.BytesDownloaded >= 4 * 20_000));
        await first.ShutdownAsync();

        var network = new FakeNetworkClient(data);
        var second = new DownloadService(network, new LocalFileStorage(), Options(), store);
        await second.RestoreAsync();

        Assert.True(await TestHelpers.WaitUntil(() => second.GetDownload(id)?.Status == DownloadStatus.Completed));
        Assert.Equal(data, await File.ReadAllBytesAsync(second.GetDownload(id)!.DestinationPath));
        // Resumed, not restarted: the new requests only asked for the missing ranges.
        Assert.All(network.Requests, r => Assert.True(r.From % 100_000 >= 20_000, $"request from {r.From} re-downloaded data"));
    }

    [Fact]
    public async Task Saved_progress_never_includes_bytes_that_are_only_in_memory()
    {
        // Crash safety: 40 KB arrive but stay in the (512 KB) write buffer. If the app died now,
        // the saved state must say 0 bytes, or a resume would skip data that never reached disk.
        using var dir = new TempDir();
        var data = TestHelpers.RandomBytes(100_000);
        var store = new JsonDownloadStateStore(dir.File("state"));
        var options = new DownloadOptions
        {
            SegmentCount = 1,
            FlushThreshold = 512 * 1024,
            StateSaveInterval = TimeSpan.FromMilliseconds(20),
        };

        var crashing = new DownloadService(StallingNetwork(data, 40_000), new LocalFileStorage(), options, store);
        var id = await crashing.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => crashing.GetDownload(id)!.BytesDownloaded == 40_000));
        await Task.Delay(200); // let several periodic saves happen

        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(0, Assert.Single(saved.Segments).BytesDownloaded);

        // ...and a "restarted" process still produces a correct file.
        var restarted = new DownloadService(new FakeNetworkClient(data), new LocalFileStorage(), options, store);
        await restarted.RestoreAsync();
        Assert.True(await TestHelpers.WaitUntil(() => restarted.GetDownload(id)?.Status == DownloadStatus.Completed));
        Assert.Equal(data, await File.ReadAllBytesAsync(restarted.GetDownload(id)!.DestinationPath));
    }

    [Fact]
    public async Task Removing_a_finished_download_forgets_it_but_keeps_the_file()
    {
        using var dir = new TempDir();
        var store = new JsonDownloadStateStore(dir.File("state"));
        var service = new DownloadService(new FakeNetworkClient(TestHelpers.RandomBytes(5_000)), new LocalFileStorage(), Options(), store);
        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => service.GetDownload(id)!.Status == DownloadStatus.Completed));
        var path = service.GetDownload(id)!.DestinationPath;

        await service.RemoveDownloadAsync(id);

        Assert.Null(service.GetDownload(id));
        Assert.Empty(await store.LoadAllAsync());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Active_downloads_cannot_be_removed()
    {
        using var dir = new TempDir();
        var service = new DownloadService(StallingNetwork(TestHelpers.RandomBytes(100_000), 1_000), new LocalFileStorage(), Options());
        var id = await service.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => service.GetDownload(id)!.Status == DownloadStatus.Downloading));

        await service.RemoveDownloadAsync(id);

        Assert.NotNull(service.GetDownload(id));
        await service.CancelDownloadAsync(id);
    }

    [Fact]
    public async Task Completed_downloads_stay_in_history_after_a_restart()
    {
        using var dir = new TempDir();
        var store = new JsonDownloadStateStore(dir.File("state"));
        var first = new DownloadService(new FakeNetworkClient(TestHelpers.RandomBytes(5_000)), new LocalFileStorage(), Options(), store);
        var id = await first.AddDownloadAsync(Url, dir.Path);
        Assert.True(await TestHelpers.WaitUntil(() => first.GetDownload(id)!.Status == DownloadStatus.Completed));

        var second = new DownloadService(new FakeNetworkClient(Array.Empty<byte>()), new LocalFileStorage(), Options(), store);
        await second.RestoreAsync();

        var restored = second.GetDownload(id);
        Assert.NotNull(restored);
        Assert.Equal(DownloadStatus.Completed, restored!.Status);
        Assert.Equal(5_000, restored.TotalBytes);
        Assert.Equal(5_000, restored.BytesDownloaded);
    }
}
