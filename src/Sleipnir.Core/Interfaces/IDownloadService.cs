using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sleipnir.Core.Models;

namespace Sleipnir.Core.Interfaces;

public interface IDownloadService
{
    /// <param name="ownerId">The user the download belongs to; null means the admin.</param>
    Task<Guid> AddDownloadAsync(string url, string outputDirectory, Guid? ownerId = null);
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
