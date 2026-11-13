namespace Sleipnir.Core.Interfaces;

public interface IFileStorage
{
    /// <summary>Creates (or truncates) a file pre-allocated to <paramref name="size"/> bytes, creating parent directories.</summary>
    Task CreateFileAsync(string path, long size);

    /// <summary>Copies <paramref name="stream"/> into the file starting at <paramref name="offset"/>.</summary>
    Task WriteAsync(string path, long offset, Stream stream, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path);
    bool Exists(string path);
    Stream OpenRead(string path);

    /// <summary>Renames <paramref name="sourcePath"/> to <paramref name="destinationPath"/>. Never overwrites.</summary>
    void Move(string sourcePath, string destinationPath);
}
