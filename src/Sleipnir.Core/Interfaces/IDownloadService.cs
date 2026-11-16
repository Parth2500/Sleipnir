using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sleipnir.Core.Models;

namespace Sleipnir.Core.Interfaces;

public interface IDownloadService
{
    /// <param name="ownerId">The user the download belongs to; null means the admin.</param>
    /// <param name="publicAddressesOnly">A member's download under Bifröst: it may not reach the home network (spec §9).</param>
    /// <param name="pending">Create it waiting for approval (spec §7.4): it does not start until <see cref="ApproveAsync"/>.</param>
    Task<Guid> AddDownloadAsync(string url, string outputDirectory, Guid? ownerId = null, bool publicAddressesOnly = false, bool pending = false);

    /// <summary>Lets a pending download start. False when it is not pending.</summary>
    Task<bool> ApproveAsync(Guid downloadId);

    /// <summary>Cancels a pending download as rejected. False when it is not pending.</summary>
    Task<bool> RejectAsync(Guid downloadId);

    /// <summary>Records that nobody answered in time. The download stays pending.</summary>
    Task MarkApprovalExpiredAsync(Guid downloadId);
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
