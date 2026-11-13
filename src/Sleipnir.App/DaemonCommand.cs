using System.Runtime.InteropServices;
using Sleipnir.Server.Hosting;
using Sleipnir.Server.Security;

namespace Sleipnir;

/// <summary><c>dnm daemon</c>: the download engine as a background process with a local API.</summary>
public static class DaemonCommand
{
    public static async Task<int> RunAsync()
    {
        var paths = AppPaths.ForCurrentUser();
        using var instanceLock = InstanceLock.TryAcquire(paths.Runtime);
        if (instanceLock is null)
        {
            Console.Error.WriteLine(InstanceMessages.AlreadyRunning(paths.Runtime));
            return 1;
        }

        using var stop = new CancellationTokenSource();
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);

        var token = LocalAdminToken.LoadOrCreate(paths.Runtime);
        var engine = EngineFactory.Create();
        await engine.RestoreAsync();

        await using var host = DaemonHost.Create(new DaemonOptions(token), engine);
        await host.StartAsync();
        DaemonInfo.Write(paths.Runtime, new DaemonInfo(host.Port, Environment.ProcessId, Version));
        Console.WriteLine($"Sleipnir daemon listening on http://127.0.0.1:{host.Port}");

        try
        {
            await host.WaitForShutdownAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Signalled: fall through to the same clean stop as the shutdown endpoint.
        }

        DaemonInfo.Delete(paths.Runtime);
        await host.StopAsync();
        await engine.ShutdownAsync(); // flush buffers and save progress so the next start resumes
        return 0;
    }

    private static string Version =>
        typeof(DaemonCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

internal static class InstanceMessages
{
    public static string AlreadyRunning(string runtimeDir) =>
        DaemonInfo.TryRead(runtimeDir) is { } info
            ? $"Sleipnir daemon is running (pid {info.Pid}, port {info.Port}). Stop it first; this is already running."
            : "Sleipnir is already running.";
}
