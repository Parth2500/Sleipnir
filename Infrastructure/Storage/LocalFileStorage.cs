using System.IO;
using System.Threading.Tasks;
using DotNetDownloadManager.Core.Interfaces;

namespace DotNetDownloadManager.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    public async Task CreateFileAsync(string path, long size)
    {
        // Pre-allocate file size
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength(size);
        await Task.CompletedTask; // FileStream constructors are sync, but keeping interface async for future flexibility
    }

    public async Task WriteAsync(string path, long offset, Stream stream)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        await stream.CopyToAsync(fs);
    }

    public Task DeleteAsync(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public bool Exists(string path)
    {
        return File.Exists(path);
    }
    
    public Stream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
