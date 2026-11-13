using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sleipnir.Server.Hosting;

namespace Sleipnir.Tests.Server;

/// <summary>A throwaway home directory plus helpers to run the real <c>dnm</c> in it.</summary>
public sealed class DnmHome : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sleipnir-home-" + Guid.NewGuid().ToString("N"));
    public string RuntimeDir => Path.Combine(Root, "run", "dnm");

    public DnmHome() => Directory.CreateDirectory(Root);

    public DnmProcess Start(params string[] args) => StartWith(new Dictionary<string, string>(), args);

    public DnmProcess StartWith(IDictionary<string, string> environment, params string[] args)
    {
        var dll = typeof(Sleipnir.UI.TuiController).Assembly.Location;
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["HOME"] = Path.Combine(Root, "home");
        info.Environment["XDG_RUNTIME_DIR"] = Path.Combine(Root, "run");
        info.Environment["XDG_DATA_HOME"] = Path.Combine(Root, "data");
        info.Environment["XDG_CONFIG_HOME"] = Path.Combine(Root, "config");
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        foreach (var (key, value) in environment) info.Environment[key] = value;
        return new DnmProcess(Process.Start(info)!);
    }

    public async Task<DaemonInfo> WaitForDaemonAsync(DnmProcess process, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (DaemonInfo.TryRead(RuntimeDir) is { } info && info.Pid == process.Id) return info;
            if (process.HasExited) throw new InvalidOperationException("dnm exited early:\n" + process.Output);
            await Task.Delay(50);
        }
        throw new TimeoutException("daemon.json never appeared:\n" + process.Output);
    }

    /// <summary>Waits until a process holds the instance lock (the lock file exists in the data folder).</summary>
    public async Task WaitForLockAsync(DnmProcess process, int seconds = 20)
    {
        var path = Path.Combine(Root, "data", "dnm", "daemon.lock");
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!File.Exists(path))
        {
            if (process.HasExited) throw new InvalidOperationException("dnm exited early:\n" + process.Output);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("lock never appeared:\n" + process.Output);
            await Task.Delay(50);
        }
    }

    public string Token => File.ReadAllText(Path.Combine(RuntimeDir, "admin.token")).Trim();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public sealed class DnmProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public DnmProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public string Output { get { lock (_output) return _output.ToString(); } }

    public async Task<int> WaitForExitAsync(int seconds = 15)
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
        return _process.ExitCode;
    }

    public void Signal(int signal) => kill(_process.Id, signal);

    public void Dispose()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
