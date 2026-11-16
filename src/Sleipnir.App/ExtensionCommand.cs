using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sleipnir.Server.Access;
using Sleipnir.Server.Bifrost;
using Sleipnir.Server.Hosting;

namespace Sleipnir;

/// <summary>
/// <c>sleipnir extension</c>: Sleipnir started by Bifröst (spec §6.2). It listens only on the private
/// socket Bifröst names, keeps its data in the folder Bifröst gives it, and opens no network port.
/// Nobody types this command.
/// </summary>
public static class ExtensionCommand
{
    public const int UsageExitCode = 64; // EX_USAGE

    public static async Task<int> RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("sleipnir extension needs Unix sockets; Bifröst runs on Linux for now.");
            return UsageExitCode;
        }
        if (ExtensionEnvironment.Read(Environment.GetEnvironmentVariable, out var missing) is not { } env)
        {
            await Console.Error.WriteLineAsync($"sleipnir extension is started by Bifröst: {missing} is not set.");
            return UsageExitCode;
        }

        try
        {
            return await RunCoreAsync(env);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(InstanceMessages.CouldNotStart(e));
            return DaemonCommand.StartFailureExitCode;
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task<int> RunCoreAsync(ExtensionEnvironment env)
    {
        using var instanceLock = InstanceLock.TryAcquire(env.DataDir);
        if (instanceLock is null)
        {
            await Console.Error.WriteLineAsync("Sleipnir is already running for this Bifröst.");
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

        var engine = EngineFactory.Create(Path.Combine(env.DataDir, "state"));
        var options = new DaemonOptions(PerPersonRoot: Path.Combine(env.DataDir, "downloads"));

        // With Bifröst's API socket, downloads can wait for the admin's approval (spec §7.4).
        using var logs = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
        using var bifrost = env.ApiSocket is { } apiSocket ? new BifrostApiClient(apiSocket) : null;
        var settings = ExtensionSettings.Load(env.DataDir, Console.Error.WriteLine);
        await using var approvals = bifrost is null ? null : new ApprovalCoordinator(engine, bifrost, settings, logs.CreateLogger("Sleipnir.Approvals"));
        await using var host = DaemonHost.Create(options, engine, new BifrostDoor(env.Socket), approvals);

        await host.StartAsync();
        try
        {
            await engine.RestoreAsync();
            approvals?.Start(); // ask again about everything still pending
            Console.WriteLine($"Sleipnir extension listening on {env.Socket}");
            try
            {
                await host.WaitForShutdownAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Bifröst stopped us: the same clean stop as the shutdown endpoint.
            }
        }
        finally
        {
            await host.StopAsync();
            if (approvals is not null) await approvals.DisposeAsync();
            await engine.ShutdownAsync(); // flush and save progress so the next start resumes
        }
        return 0;
    }
}
