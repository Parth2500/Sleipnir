using Sleipnir.Infrastructure.Network;
using Sleipnir.Infrastructure.Persistence;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Services;

namespace Sleipnir;

/// <summary>Builds the download engine exactly one way, for the TUI and the daemon alike.</summary>
public static class EngineFactory
{
    public static DownloadService Create() => Create(JsonDownloadStateStore.DefaultDirectory());

    /// <summary>The engine with its download records in <paramref name="stateDirectory"/> (under Bifröst, the extension's data folder).</summary>
    public static DownloadService Create(string stateDirectory) => new(
        new HttpNetworkClient(),
        new LocalFileStorage(),
        new DownloadOptions(),
        new JsonDownloadStateStore(stateDirectory));
}
