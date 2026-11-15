using System.Net.Sockets;
using System.Text;
using Sleipnir.Server.Access;
using Sleipnir.Server.Hosting;
using Sleipnir.Tests.Tui;

namespace Sleipnir.Tests.Server;

/// <summary>
/// Sleipnir as a Bifröst extension: a real host behind the Bifröst door on a temp Unix socket, and
/// a client that plays Bifröst by sending the identity headers itself.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ExtensionFixture : IAsyncDisposable
{
    public const string Data = "/bifrost-data/x/sleipnir";

    public required DaemonHost Host { get; init; }
    public required HttpClient Http { get; init; }
    public required StubDownloadService Downloads { get; init; }
    public required string Folder { get; init; }
    public string SocketPath => Path.Combine(Folder, "extension.sock");

    public static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid(), Device = Guid.NewGuid();

    public static async Task<ExtensionFixture> StartAsync(Action<string>? beforeStart = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sleipnir-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var socket = Path.Combine(folder, "extension.sock");
        beforeStart?.Invoke(socket);
        var downloads = new StubDownloadService();
        var host = DaemonHost.Create(new DaemonOptions(PerPersonRoot: Path.Combine(Data, "downloads")), downloads, new BifrostDoor(socket));
        await host.StartAsync();
        return new ExtensionFixture { Host = host, Http = ClientFor(socket), Downloads = downloads, Folder = folder };
    }

    public static HttpClient ClientFor(string socket) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
            return new NetworkStream(s, ownsSocket: true);
        },
    }) { BaseAddress = new Uri("http://localhost") };

    /// <summary>A request as Bifröst would forward it: the four identity headers, or none when <paramref name="person"/> is null.</summary>
    public Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid? person, string role = "member",
        string kind = "app", string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (person is { } id)
        {
            request.Headers.Add("X-Bifrost-Person-Id", id.ToString());
            request.Headers.Add("X-Bifrost-Role", role);
            request.Headers.Add("X-Bifrost-Device-Id", Device.ToString());
            request.Headers.Add("X-Bifrost-Device-Kind", kind);
        }
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return Http.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}
