using System.Text.Json;

namespace Sleipnir.Server.Hosting;

/// <summary>What <c>daemon.json</c> tells local clients: where the daemon listens and who it is.</summary>
public sealed record DaemonInfo(int Port, int Pid, string Version)
{
    private const string FileName = "daemon.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Write(string runtimeDir, DaemonInfo info)
    {
        InstanceLock.CreateRuntimeDirectory(runtimeDir);
        var path = Path.Combine(runtimeDir, FileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, info, Json);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static DaemonInfo? TryRead(string runtimeDir)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(runtimeDir, FileName));
            return JsonSerializer.Deserialize<DaemonInfo>(stream, Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string runtimeDir)
    {
        try { File.Delete(Path.Combine(runtimeDir, FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
