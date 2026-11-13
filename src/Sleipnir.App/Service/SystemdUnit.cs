namespace Sleipnir.Service;

public static class SystemdUnit
{
    public const string Name = "sleipnir.service";

    /// <summary>The systemd user unit that keeps <c>dnm daemon</c> running.</summary>
    public static string Render(string executablePath)
    {
        // systemd expands % and $ inside ExecStart; double them so the path is taken literally.
        var path = executablePath.Replace("%", "%%").Replace("$", "$$");
        return "[Unit]\nDescription=Sleipnir download manager\n\n"
             + $"[Service]\nExecStart=\"{path}\" daemon\nRestart=on-failure\nRestartSec=5\nRestartPreventExitStatus=1\n\n"
             + "[Install]\nWantedBy=default.target\n";
    }
}
