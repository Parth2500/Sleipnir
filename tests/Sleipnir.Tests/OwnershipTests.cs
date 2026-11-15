using System.Text.Json.Nodes;
using Sleipnir.Core.Models;
using Sleipnir.Infrastructure.Persistence;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;
using Sleipnir.Tests.Fakes;

namespace Sleipnir.Tests;

public class OwnershipTests
{
    private const string Url = "http://example.test/files/file.bin";

    private static DownloadOptions Options() => new()
    {
        SegmentCount = 2,
        MinSegmentSize = 1,
        RetryDelay = TimeSpan.Zero,
        StateSaveInterval = TimeSpan.FromMilliseconds(50),
    };

    private static DownloadService Service(JsonDownloadStateStore store) =>
        new(new FakeNetworkClient(TestHelpers.RandomBytes(5_000)), new LocalFileStorage(), Options(), store);

    [Fact]
    public async Task A_download_remembers_who_added_it()
    {
        using var dir = new TempDir();
        var owner = Guid.NewGuid();
        var service = Service(new JsonDownloadStateStore(dir.File("state")));

        var mine = await service.AddDownloadAsync(Url, dir.Path, owner);
        var nobodys = await service.AddDownloadAsync(Url + "2", dir.Path);

        Assert.Equal(owner, service.GetDownload(mine)!.OwnerId);
        Assert.Null(service.GetDownload(nobodys)!.OwnerId);
    }

    [Fact]
    public async Task The_owner_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new JsonDownloadStateStore(dir.File("state"));
        var owner = Guid.NewGuid();
        var first = Service(store);
        var id = await first.AddDownloadAsync(Url, dir.Path, owner);
        Assert.True(await TestHelpers.WaitUntil(() => first.GetDownload(id)!.Status == DownloadStatus.Completed));

        var second = Service(store);
        await second.RestoreAsync();

        Assert.Equal(owner, second.GetDownload(id)!.OwnerId);
    }

    [Fact]
    public async Task Records_saved_before_owners_existed_load_as_ownerless()
    {
        using var dir = new TempDir();
        var stateDir = dir.File("state");
        var store = new JsonDownloadStateStore(stateDir);
        var record = new DownloadRecord(Guid.NewGuid(), Url, "/x/file.bin", "file.bin", 100, true, true,
            DownloadStatus.Paused, DateTime.UtcNow, null, new[] { new SegmentRecord(0, 0, 99, 42, false) },
            OwnerId: Guid.NewGuid());
        await store.SaveAsync(record);
        var path = Directory.GetFiles(stateDir, "*.json").Single();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var key = json.Select(p => p.Key).Single(k => k.Equals("ownerId", StringComparison.OrdinalIgnoreCase));
        json.Remove(key);
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var loaded = Assert.Single(await store.LoadAllAsync());

        Assert.Null(loaded.OwnerId);
        Assert.Equal(42, loaded.Segments.Single().BytesDownloaded);
    }

    [Fact]
    public async Task A_public_only_download_asks_the_network_for_public_addresses_every_time()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(5_000));
        var service = new DownloadService(network, new LocalFileStorage(), Options(), new JsonDownloadStateStore(dir.File("state")));

        var id = await service.AddDownloadAsync(Url, dir.Path, Guid.NewGuid(), publicAddressesOnly: true);
        Assert.True(await TestHelpers.WaitUntil(() => service.GetDownload(id)!.Status == DownloadStatus.Completed));

        Assert.True(service.GetDownload(id)!.PublicAddressesOnly);
        Assert.NotEmpty(network.PublicOnly);
        Assert.All(network.PublicOnly, Assert.True);
    }

    [Fact]
    public async Task A_destination_that_is_not_public_fails_at_once_without_retries()
    {
        using var dir = new TempDir();
        var network = new FakeNetworkClient(TestHelpers.RandomBytes(5_000)) { RefusePublicOnly = true };
        var service = new DownloadService(network, new LocalFileStorage(), Options(), new JsonDownloadStateStore(dir.File("state")));

        var id = await service.AddDownloadAsync(Url, dir.Path, Guid.NewGuid(), publicAddressesOnly: true);
        Assert.True(await TestHelpers.WaitUntil(() => service.GetDownload(id)!.Status == DownloadStatus.Failed));

        Assert.Single(network.PublicOnly);
        Assert.Contains("not a public address", service.GetDownload(id)!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flagged_task_survives_a_restart_still_public_only()
    {
        using var dir = new TempDir();
        var store = new JsonDownloadStateStore(dir.File("state"));
        var first = Service(store);
        var id = await first.AddDownloadAsync(Url, dir.Path, Guid.NewGuid(), publicAddressesOnly: true);
        Assert.True(await TestHelpers.WaitUntil(() => first.GetDownload(id)!.Status == DownloadStatus.Completed));

        var second = Service(store);
        await second.RestoreAsync();

        Assert.True(second.GetDownload(id)!.PublicAddressesOnly);
    }
}
