using System.Runtime.InteropServices;
using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.Infrastructure.Network;
using DotNetDownloadManager.Infrastructure.Persistence;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.UI;
using Spectre.Console;

// --- Composition root ---
var stateStore = new JsonDownloadStateStore(JsonDownloadStateStore.DefaultDirectory());
var downloadService = new DownloadService(new HttpNetworkClient(), new LocalFileStorage(), new DownloadOptions(), stateStore);
var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
var ui = new TuiController(downloadService, outputDirectory);
var speeds = new SpeedTracker();

// Bring back downloads from the previous session; interrupted ones resume automatically.
await downloadService.RestoreAsync();

// SIGTERM (`kill`, `podman stop`) and SIGHUP (terminal closed) exit gracefully, saving progress.
// Ctrl+C is read as a key so the UI can ask for confirmation; SIGINT is the fallback if that isn't possible.
var exitSignalled = false;
void OnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    Volatile.Write(ref exitSignalled, true);
}
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

var interactive = !Console.IsInputRedirected;
if (interactive)
{
    try { Console.TreatControlCAsInput = true; } catch (IOException) { /* keep the SIGINT fallback */ }
}

(int Width, int Height) TerminalSize()
{
    try { return (Console.WindowWidth, Console.WindowHeight); }
    catch (IOException) { return (100, 30); }
}

// Full-screen app: draw on the terminal's alternate screen so the shell is restored on exit.
var alternateScreen = AnsiConsole.Profile.Capabilities.Ansi && interactive;
if (alternateScreen) Console.Write("\x1b[?1049h\x1b[H");

try
{
    var size = TerminalSize();
    await AnsiConsole.Live(TuiRenderer.Render(ui, speeds, size.Width, size.Height))
        .AutoClear(true)
        .Overflow(VerticalOverflow.Crop)
        .Cropping(VerticalOverflowCropping.Bottom)
        .StartAsync(async ctx =>
        {
            while (!ui.QuitRequested && !Volatile.Read(ref exitSignalled))
            {
                // Handle everything typed (or pasted) since the last frame.
                while (interactive && Console.KeyAvailable)
                {
                    await ui.HandleKeyAsync(Console.ReadKey(intercept: true));
                }

                speeds.Update(downloadService.GetAllDownloads(), DateTime.UtcNow);
                size = TerminalSize();
                ctx.UpdateTarget(TuiRenderer.Render(ui, speeds, size.Width, size.Height));
                await Task.Delay(50);
            }
        });
}
finally
{
    if (alternateScreen) Console.Write("\x1b[?1049l");
}

// Stop transfers cleanly: buffered data is flushed and progress saved, so the next start resumes.
AnsiConsole.MarkupLine("[grey62]Saving progress…[/]");
await downloadService.ShutdownAsync();
var unfinished = downloadService.GetAllDownloads().Count(d => d.Status == DownloadStatus.Queued);
AnsiConsole.MarkupLine(unfinished > 0
    ? $"[grey62]{unfinished} download{(unfinished == 1 ? "" : "s")} will resume next time you start Sleipnir.[/]"
    : "[grey62]Bye.[/]");
