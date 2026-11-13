using System.Net;
using System.Net.Http.Headers;

namespace Sleipnir.Tests.Server;

public class DaemonProcessTests
{
    private const int SIGKILL = 9, SIGTERM = 15;

    private static HttpClient ClientFor(int port, string? token = null)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    [Fact]
    public async Task The_daemon_starts_publishes_where_it_listens_and_guards_its_token()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var daemon = home.Start("daemon");

        var info = await home.WaitForDaemonAsync(daemon);

        Assert.Equal(daemon.Id, info.Pid);
        using var anonymous = ClientFor(info.Port);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/server")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/downloads")).StatusCode);
        using var admin = ClientFor(info.Port, home.Token);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/downloads")).StatusCode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(home.RuntimeDir, "admin.token")));
    }

    [Fact]
    public async Task A_second_daemon_refuses_to_start()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var first = home.Start("daemon");
        await home.WaitForDaemonAsync(first);

        using var second = home.Start("daemon");

        Assert.Equal(1, await second.WaitForExitAsync());
        Assert.Contains("already running", second.Output);
        Assert.False(first.HasExited);
    }

    [Fact]
    public async Task The_TUI_refuses_to_start_while_a_daemon_runs()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var daemon = home.Start("daemon");
        await home.WaitForDaemonAsync(daemon);

        using var tui = home.Start();

        Assert.Equal(1, await tui.WaitForExitAsync());
        Assert.Contains("daemon is running", tui.Output);
    }

    [Fact]
    public async Task A_daemon_refuses_to_start_while_the_TUI_runs()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var tui = home.Start();
        await Task.Delay(TimeSpan.FromSeconds(3)); // the TUI takes the lock before drawing anything

        using var daemon = home.Start("daemon");

        Assert.Equal(1, await daemon.WaitForExitAsync());
        Assert.Contains("already running", daemon.Output);
    }

    [Fact]
    public async Task The_shutdown_endpoint_stops_the_daemon_cleanly()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var daemon = home.Start("daemon");
        var info = await home.WaitForDaemonAsync(daemon);

        using var admin = ClientFor(info.Port, home.Token);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/server/shutdown", null)).StatusCode);

        Assert.Equal(0, await daemon.WaitForExitAsync(10));
        Assert.False(File.Exists(Path.Combine(home.RuntimeDir, "daemon.json")));
    }

    [Fact]
    public async Task SIGTERM_stops_the_daemon_cleanly()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var daemon = home.Start("daemon");
        await home.WaitForDaemonAsync(daemon);

        daemon.Signal(SIGTERM);

        Assert.Equal(0, await daemon.WaitForExitAsync(10));
        Assert.False(File.Exists(Path.Combine(home.RuntimeDir, "daemon.json")));
    }

    [Fact]
    public async Task After_a_crash_a_new_daemon_starts_despite_the_leftover_files()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var crashed = home.Start("daemon");
        await home.WaitForDaemonAsync(crashed);
        crashed.Signal(SIGKILL);
        await crashed.WaitForExitAsync(10);

        using var next = home.Start("daemon");
        var info = await home.WaitForDaemonAsync(next);

        Assert.Equal(next.Id, info.Pid);
    }

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new DnmHome();
        using var process = home.Start("frobnicate");

        Assert.Equal(2, await process.WaitForExitAsync());
        Assert.Contains("daemon", process.Output);
    }
}
