using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Sleipnir.Server.Hosting;
using Sleipnir.Server.Identity;
using Sleipnir.Server.Persistence;
using Sleipnir.Server.Security;

namespace Sleipnir;

/// <summary><c>dnm daemon</c>: the download engine as a background process with a local API.</summary>
public static class DaemonCommand
{
    /// <summary>Exit code for "Sleipnir could not start" (sysexits EX_IOERR); 1 means another instance is running.</summary>
    public const int StartFailureExitCode = 74;

    public static async Task<int> RunAsync()
    {
        try
        {
            return await RunCoreAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine(InstanceMessages.CouldNotStart(e));
            return StartFailureExitCode;
        }
    }

    private static async Task<int> RunCoreAsync()
    {
        var paths = AppPaths.ForCurrentUser();
        // The lock lives next to the data it protects, so it holds even when two processes disagree
        // about the runtime folder (a systemd service has XDG_RUNTIME_DIR, a cron job does not).
        using var instanceLock = InstanceLock.TryAcquire(paths.Data);
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
        using var database = Database.OpenFile(Path.Combine(paths.Data, "dnm.db"));
        await Database.MigrateAsync(database);
        var identity = new IdentityService(database);
        await identity.EnsureLocalAdminAsync(Environment.UserName, token);

        var engine = EngineFactory.Create();
        await using var host = DaemonHost.Create(new DaemonOptions(), engine, identity);

        // Listen first: if that fails nothing has been started that would need a clean shutdown.
        await host.StartAsync();
        try
        {
            await engine.RestoreAsync();
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
        }
        finally
        {
            DaemonInfo.Delete(paths.Runtime);
            await host.StopAsync();
            await engine.ShutdownAsync(); // flush buffers and save progress so the next start resumes
        }
        return 0;
    }

    private static string Version =>
        typeof(DaemonCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

internal static class InstanceMessages
{
    public static string AlreadyRunning(string runtimeDir) =>
        DaemonInfo.TryRead(runtimeDir) is { } info
            ? $"Sleipnir is already running (pid {info.Pid}, port {info.Port})."
            : "Sleipnir is already running.";

    /// <summary>What the interactive screen says: it cannot share the downloads with a running daemon yet.</summary>
    public static string DaemonBlocksScreen(string runtimeDir) =>
        DaemonInfo.TryRead(runtimeDir) is { } info
            ? $"A Sleipnir daemon is running (pid {info.Pid}, port {info.Port}). Stop it first; the screen will connect to it in a later release."
            : "Sleipnir is already running.";

    public static string CouldNotStart(Exception e) => $"Sleipnir could not start: {e.Message}";
}
