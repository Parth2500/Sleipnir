namespace Sleipnir.Service;

/// <summary><c>dnm service install | uninstall | status</c> for systemd user services.</summary>
public sealed class ServiceCommand(ICommandRunner runner, string configHome, string executablePath, TextWriter output)
{
    private string UnitPath => Path.Combine(configHome, "systemd", "user", SystemdUnit.Name);

    public Task<int> RunAsync(string[] args) => (args.FirstOrDefault()) switch
    {
        "install" => InstallAsync(),
        "uninstall" => UninstallAsync(),
        "status" => StatusAsync(),
        _ => Usage(),
    };

    private async Task<int> InstallAsync()
    {
        if (executablePath.Contains('"') || executablePath.Contains('\n') || executablePath.Contains('\r'))
        {
            output.WriteLine("Cannot install: the path to dnm contains a quote or a line break.");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        await File.WriteAllTextAsync(UnitPath, SystemdUnit.Render(executablePath));

        foreach (var args in new[] { new[] { "daemon-reload" }, new[] { "enable", "--now", SystemdUnit.Name } })
        {
            var (code, text) = await Systemctl(args);
            if (code != 0)
            {
                output.WriteLine($"systemctl {args[0]} failed:");
                output.WriteLine(text);
                return 1;
            }
        }

        output.WriteLine($"Installed and started {SystemdUnit.Name} ({UnitPath}).");
        output.WriteLine("To keep it running when you are logged out and start it at boot: loginctl enable-linger $USER");
        return 0;
    }

    private async Task<int> UninstallAsync()
    {
        await Systemctl("disable", "--now", SystemdUnit.Name); // not installed is fine
        if (File.Exists(UnitPath)) File.Delete(UnitPath);
        await Systemctl("daemon-reload");
        output.WriteLine($"Removed {SystemdUnit.Name}.");
        return 0;
    }

    private async Task<int> StatusAsync()
    {
        var (code, text) = await Systemctl("status", SystemdUnit.Name);
        output.WriteLine(text);
        return code;
    }

    private Task<int> Usage()
    {
        output.WriteLine("Usage: dnm service install | uninstall | status");
        return Task.FromResult(2);
    }

    private Task<(int ExitCode, string Output)> Systemctl(params string[] args) =>
        runner.RunAsync("systemctl", ["--user", .. args]);
}
