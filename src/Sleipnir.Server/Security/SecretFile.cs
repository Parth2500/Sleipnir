using System.Text;

namespace Sleipnir.Server.Security;

/// <summary>Creates files only their owner can read, with the permissions set before any content is written.</summary>
public static class SecretFile
{
    /// <exception cref="IOException">The file already exists.</exception>
    public static void CreateNew(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
