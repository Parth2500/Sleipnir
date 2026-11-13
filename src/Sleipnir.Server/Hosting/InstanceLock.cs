namespace Sleipnir.Server.Hosting;

/// <summary>
/// Makes sure only one process per user drives the download state. It is an exclusive open file handle,
/// so the OS releases it when the process dies; a leftover file never blocks a new start.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    public static InstanceLock? TryAcquire(string runtimeDir)
    {
        CreateRuntimeDirectory(runtimeDir);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new InstanceLock(new FileStream(Path.Combine(runtimeDir, "daemon.lock"), options));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();

    internal static void CreateRuntimeDirectory(string runtimeDir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(runtimeDir);
        else Directory.CreateDirectory(runtimeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
