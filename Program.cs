using Spectre.Console;
using System.Runtime.InteropServices;
using DotNetDownloadManager.Infrastructure.Network;
using DotNetDownloadManager.Infrastructure.Persistence;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.UI;
using Spectre.Console.Rendering;

// --- Initialize Services ---
var network = new HttpNetworkClient();
var storage = new LocalFileStorage();
var stateStore = new JsonDownloadStateStore(JsonDownloadStateStore.DefaultDirectory());
var downloadService = new DownloadService(network, storage, new DownloadOptions(), stateStore);
var speeds = new SpeedTracker();

// Bring back downloads from the previous session; interrupted ones resume automatically.
await downloadService.RestoreAsync();

// Create the main dashboard layout
var layout = new Layout("Root")
    .SplitRows(
        new Layout("Header").Size(10),
        new Layout("Body"),
        new Layout("Footer").Size(3));

layout["Body"].SplitColumns(
    new Layout("Left"),
    new Layout("Right"));

// Track current download: start with the most recent one, if any
Guid? currentDownloadId = downloadService.GetAllDownloads()
    .OrderByDescending(d => d.CreatedAt)
    .Select(d => (Guid?)d.Id)
    .FirstOrDefault();
var shouldExit = false;
bool needsInput = false;

// Ctrl+C, `kill`/`podman stop` (SIGTERM) and closing the terminal (SIGHUP) exit gracefully, saving progress.
// A second Ctrl+C while shutting down exits immediately.
void RequestExit(PosixSignalContext context)
{
    if (Volatile.Read(ref shouldExit)) return;
    context.Cancel = true;
    Volatile.Write(ref shouldExit, true);
}
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestExit);
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestExit);
using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, RequestExit);

// Initialize layout with content BEFORE starting Live display
layout["Header"].Update(DashboardRenderer.BuildHeader());
layout["Left"].Update(DashboardRenderer.BuildCurrentDownloadPanel(downloadService, currentDownloadId, speeds));
layout["Right"].Update(DashboardRenderer.BuildHistoryPanel(downloadService, speeds));
layout["Footer"].Update(DashboardRenderer.BuildFooter(downloadService, currentDownloadId, speeds));

while (!Volatile.Read(ref shouldExit))
{
    await AnsiConsole.Live(layout)
        .AutoClear(false)
        .StartAsync(async ctx =>
        {
            while (!Volatile.Read(ref shouldExit) && !needsInput)
            {
                speeds.Update(downloadService.GetAllDownloads(), DateTime.UtcNow);

                // Update Header
                layout["Header"].Update(DashboardRenderer.BuildHeader());

                // Update Left Panel - Current Download
                layout["Left"].Update(DashboardRenderer.BuildCurrentDownloadPanel(downloadService, currentDownloadId, speeds));

                // Update Right Panel - History
                layout["Right"].Update(DashboardRenderer.BuildHistoryPanel(downloadService, speeds));

                // Update Footer - Input/Controls
                layout["Footer"].Update(DashboardRenderer.BuildFooter(downloadService, currentDownloadId, speeds));

                ctx.Refresh();

                // Check for keyboard input
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    // Handle download controls
                    if (currentDownloadId.HasValue)
                    {
                        if (key.Key == ConsoleKey.P)
                        {
                            await downloadService.PauseDownloadAsync(currentDownloadId.Value);
                        }
                        else if (key.Key == ConsoleKey.R)
                        {
                            await downloadService.ResumeDownloadAsync(currentDownloadId.Value);
                        }
                        else if (key.Key == ConsoleKey.C)
                        {
                            await downloadService.CancelDownloadAsync(currentDownloadId.Value);
                        }
                    }

                    // Select another download (same order as the history panel)
                    if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                    {
                        var ids = downloadService.GetAllDownloads()
                            .OrderByDescending(d => d.CreatedAt)
                            .Take(10)
                            .Select(d => d.Id)
                            .ToList();
                        if (ids.Count > 0)
                        {
                            var index = currentDownloadId.HasValue ? ids.IndexOf(currentDownloadId.Value) : -1;
                            index = key.Key == ConsoleKey.UpArrow ? index - 1 : index + 1;
                            currentDownloadId = ids[(index % ids.Count + ids.Count) % ids.Count];
                        }
                    }

                    // Handle new download (N key)
                    if (key.Key == ConsoleKey.N)
                    {
                        needsInput = true;
                        break; // Exit live display to get input
                    }

                    // Exit on Q
                    if (key.Key == ConsoleKey.Q)
                    {
                        shouldExit = true;
                        break;
                    }
                }

                await Task.Delay(100);
            }
        });

    // Handle input outside of Live display
    if (needsInput)
    {
        needsInput = false;
        
        var url = AnsiConsole.Ask<string>("[green]Enter URL to download:[/]");
        
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            var outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "Downloads");
            
            currentDownloadId = await downloadService.AddDownloadAsync(url, outputDirectory);
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLine("[red]Invalid URL provided.[/]");
            await Task.Delay(2000);
        }
    }
}

// Stop transfers cleanly: buffered data is flushed and progress saved, so the next start resumes.
AnsiConsole.MarkupLine("[dim]Saving progress...[/]");
await downloadService.ShutdownAsync();
