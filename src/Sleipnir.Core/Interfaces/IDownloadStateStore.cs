using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Core.Interfaces;

/// <summary>Persists download state so downloads survive an application restart.</summary>
public interface IDownloadStateStore
{
    Task SaveAsync(DownloadRecord record);
    Task<IReadOnlyList<DownloadRecord>> LoadAllAsync();
    Task DeleteAsync(Guid id);
}
