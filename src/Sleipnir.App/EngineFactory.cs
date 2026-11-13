using Sleipnir.Infrastructure.Network;
using Sleipnir.Infrastructure.Persistence;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;

namespace Sleipnir;

/// <summary>Builds the download engine exactly one way, for the TUI and the daemon alike.</summary>
public static class EngineFactory
{
    public static DownloadService Create() => new(
        new HttpNetworkClient(),
        new LocalFileStorage(),
        new DownloadOptions(),
        new JsonDownloadStateStore(JsonDownloadStateStore.DefaultDirectory()));
}
