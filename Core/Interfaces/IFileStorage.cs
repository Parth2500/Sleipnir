using System.IO;
using System.Threading.Tasks;

namespace DotNetDownloadManager.Core.Interfaces;

public interface IFileStorage
{
    Task CreateFileAsync(string path, long size);
    Task WriteAsync(string path, long offset, Stream stream);
    Task DeleteAsync(string path);
    bool Exists(string path);
    Stream OpenRead(string path);
}
