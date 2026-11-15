using System.Runtime.InteropServices;
using Sleipnir.Server.Access;
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
        await using var host = DaemonHost.Create(options, engine, new BifrostDoor(env.Socket));

        await host.StartAsync();
        try
        {
            await engine.RestoreAsync();
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
            await engine.ShutdownAsync(); // flush and save progress so the next start resumes
        }
        return 0;
    }
}
