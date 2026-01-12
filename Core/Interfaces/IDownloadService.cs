using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DotNetDownloadManager.Core.Models;

namespace DotNetDownloadManager.Core.Interfaces;

public interface IDownloadService
{
    Task<Guid> AddDownloadAsync(string url, string outputDirectory);
    Task PauseDownloadAsync(Guid downloadId);
    Task ResumeDownloadAsync(Guid downloadId);
    Task CancelDownloadAsync(Guid downloadId);
    IEnumerable<DownloadTask> GetAllDownloads();
    DownloadTask? GetDownload(Guid downloadId);
}
