using Sleipnir.Core.Interfaces;

namespace Sleipnir.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    public Task CreateFileAsync(string path, long size)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Pre-allocate so every segment can write at its own offset.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.SetLength(size);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string path, long offset, Stream stream, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await stream.CopyToAsync(fs, cancellationToken);
    }

    public Task DeleteAsync(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public bool Exists(string path) => File.Exists(path);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);
}
