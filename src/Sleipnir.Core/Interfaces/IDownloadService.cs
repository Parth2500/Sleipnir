using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sleipnir.Core.Models;

namespace Sleipnir.Core.Interfaces;

public interface IDownloadService
{
    Task<Guid> AddDownloadAsync(string url, string outputDirectory);
    Task PauseDownloadAsync(Guid downloadId);
    Task ResumeDownloadAsync(Guid downloadId);
    Task CancelDownloadAsync(Guid downloadId);

    /// <summary>Removes a finished (completed or cancelled) download from the list and saved history. The file itself is kept.</summary>
    Task RemoveDownloadAsync(Guid downloadId);
    IEnumerable<DownloadTask> GetAllDownloads();
    DownloadTask? GetDownload(Guid downloadId);

    /// <summary>Loads downloads saved by a previous session. Interrupted ones are queued again.</summary>
    Task RestoreAsync();

    /// <summary>Stops all transfers, saving their progress so the next session can resume them.</summary>
    Task ShutdownAsync();
}
