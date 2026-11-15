using System.Diagnostics;
using System.Text.Json;

namespace Sleipnir.Tests.Server;

public sealed class ExtensionProcessTests : IDisposable
{
    private const int SIGTERM = 15;
    private readonly SleipnirHome _home = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sleipnir-extproc-" + Guid.NewGuid().ToString("N")[..8]);

    public ExtensionProcessTests() =>
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose()
    {
        _home.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Socket => Path.Combine(_root, "extension.sock");

    private SleipnirProcess Start(bool withSocket = true)
    {
        var env = new Dictionary<string, string>
        {
            ["BIFROST_DATA_DIR"] = Path.Combine(_root, "data"),
            ["BIFROST_EXTENSION"] = "sleipnir",
            ["BIFROST_INTERFACE"] = "1",
        };
        if (withSocket) env["BIFROST_SOCKET"] = Socket;
        return _home.StartWith(env, "extension");
    }

    [Fact]
    public async Task Without_the_environment_extension_mode_refuses_to_start()
    {
        using var process = Start(withSocket: false);

        Assert.Equal(64, await process.WaitForExitAsync());
        Assert.Contains("BIFROST_SOCKET", process.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starts_on_the_socket_opens_no_TCP_port_and_stops_on_SIGTERM()
    {
        using var process = Start();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(Socket))
        {
            if (process.HasExited || DateTime.UtcNow > deadline) throw new InvalidOperationException("no socket:\n" + process.Output);
            await Task.Delay(50);
        }
        await Task.Delay(200);

        using var http = ExtensionFixture.ClientFor(Socket);
        var info = JsonDocument.Parse(await http.GetStringAsync("/api/v1/server")).RootElement;

        Assert.Equal("extension", info.GetProperty("mode").GetString());
        Assert.Empty(ListeningTcpInodes().Intersect(SocketInodes(process.Id)));
        process.Signal(SIGTERM);
        Assert.Equal(0, await process.WaitForExitAsync(10));
    }

    private static IEnumerable<string> SocketInodes(int pid) =>
        Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd")
            .Select(fd => new FileInfo(fd).LinkTarget)
            .Where(t => t is not null && t.StartsWith("socket:[", StringComparison.Ordinal))
            .Select(t => t![8..^1]);

    /// <summary>Inodes of every listening TCP socket (state 0A) in this network namespace.</summary>
    private static IEnumerable<string> ListeningTcpInodes() =>
        new[] { "/proc/net/tcp", "/proc/net/tcp6" }.Where(File.Exists)
            .SelectMany(path => File.ReadLines(path).Skip(1))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(cols => cols[3] == "0A")
            .Select(cols => cols[9]);
}
