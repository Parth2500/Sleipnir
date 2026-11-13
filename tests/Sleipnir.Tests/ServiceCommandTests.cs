using Sleipnir.Service;

namespace Sleipnir.Tests;

public class ServiceCommandTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "sleipnir-config-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private readonly StringWriter _output = new();

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private ServiceCommand Command(string exe = "/opt/dnm") => new(_runner, _config, exe, _output);
    private string UnitPath => Path.Combine(_config, "systemd", "user", "sleipnir.service");

    private sealed class FakeRunner : ICommandRunner
    {
        public List<string> Calls { get; } = new();
        public Func<string, (int, string)> Respond { get; set; } = _ => (0, "");

        public Task<(int ExitCode, string Output)> RunAsync(string file, params string[] args)
        {
            var line = file + " " + string.Join(' ', args);
            Calls.Add(line);
            return Task.FromResult(Respond(line));
        }
    }

    [Fact]
    public void The_unit_runs_the_daemon_and_restarts_it_on_failure()
    {
        const string expected = "[Unit]\nDescription=Sleipnir download manager\n\n[Service]\nExecStart=\"/opt/dnm\" daemon\nRestart=on-failure\nRestartSec=5\nRestartPreventExitStatus=1\n\n[Install]\nWantedBy=default.target\n";

        Assert.Equal(expected, SystemdUnit.Render("/opt/dnm"));
    }

    [Fact]
    public void Paths_with_spaces_stay_quoted_and_systemd_specifiers_are_escaped()
    {
        var unit = SystemdUnit.Render("/opt/my apps/100%/d$nm");

        Assert.Contains("ExecStart=\"/opt/my apps/100%%/d$$nm\" daemon", unit);
    }

    [Fact]
    public async Task Install_writes_the_unit_then_reloads_and_enables_it()
    {
        var code = await Command().RunAsync(["install"]);

        Assert.Equal(0, code);
        Assert.Equal(SystemdUnit.Render("/opt/dnm"), File.ReadAllText(UnitPath));
        Assert.Equal(new[] { "systemctl --user daemon-reload", "systemctl --user enable --now sleipnir.service" }, _runner.Calls);
        Assert.Contains("loginctl enable-linger", _output.ToString());
    }

    [Fact]
    public async Task Install_reports_a_systemctl_failure()
    {
        _runner.Respond = line => line.Contains("enable") ? (1, "Failed to connect to bus") : (0, "");

        var code = await Command().RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("Failed to connect to bus", _output.ToString());
    }

    [Theory]
    [InlineData("/opt/we\"ird/dnm")]
    [InlineData("/opt/new\nline/dnm")]
    public async Task Install_refuses_paths_it_cannot_quote_safely(string exe)
    {
        var code = await Command(exe).RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.False(File.Exists(UnitPath));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Uninstall_disables_removes_and_reloads_even_if_nothing_was_installed()
    {
        var code = await Command().RunAsync(["uninstall"]);

        Assert.Equal(0, code);
        Assert.Equal(new[] { "systemctl --user disable --now sleipnir.service", "systemctl --user daemon-reload" }, _runner.Calls);

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, "x");
        await Command().RunAsync(["uninstall"]);
        Assert.False(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Status_passes_through_the_systemctl_result()
    {
        _runner.Respond = _ => (3, "inactive (dead)");

        var code = await Command().RunAsync(["status"]);

        Assert.Equal(3, code);
        Assert.Equal(new[] { "systemctl --user status sleipnir.service" }, _runner.Calls);
        Assert.Contains("inactive (dead)", _output.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("explode")]
    public async Task Missing_or_unknown_subcommands_print_usage(params string[] args)
    {
        var code = await Command().RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage: dnm service install | uninstall | status", _output.ToString());
        Assert.Empty(_runner.Calls);
    }
}
