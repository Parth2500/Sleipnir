using Spectre.Console;
using DotNetDownloadManager.Infrastructure.Network;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.Core.Models;
using System.Text;

var title = new FigletText("DotNet Download Manager")
{
    Justification = Justify.Left,
    Color = Color.Blue
};
AnsiConsole.Write(title);

AnsiConsole.MarkupLine("[bold green]Core system initialized![/]");

// --- Main Application Logic ---
var network = new HttpNetworkClient();
var storage = new LocalFileStorage();
var downloadService = new DownloadService(network, storage);

// Define a file to download (using a safe test file)
// Using a 10MB test file from generic speed test source or similar. 
// Let's use a reliable placeholder or just the Google Robots for brevity/safety if speedtest is flaky.
// Actually, let's try a small legitimate ISO or installer if possible, or just ash sample. 
// Let's stick to the previous robot.txt for speed, OR a slightly larger file to see progress.
// I'll use a sample 10MB file from a mock CDN or just a larger text file.
// Let's use: https://speed.hetzner.de/100MB.bin (might be too big).
// Let's use a 5MB test file. 
var url = "https://www.sampledocs.in/DownloadFiles/SampleFile?filename=sampledocs-100mb-pdf-file&ext=pdf"; // 100MB might take too long for quick verify, let's just watch it for a few seconds.

// var outputDirectory = Directory.GetCurrentDirectory();
var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

AnsiConsole.MarkupLine($"[yellow]Starting download: {url}[/]");
AnsiConsole.MarkupLine($"[yellow]Output Directory: {outputDirectory}[/]");

var downloadId = await downloadService.AddDownloadAsync(url, outputDirectory);

await AnsiConsole.Progress()
    .Columns(new ProgressColumn[] 
    {
        new TaskDescriptionColumn(),
        new ProgressBarColumn(),
        new PercentageColumn(),
        new RemainingTimeColumn(),
        new SpinnerColumn(),
        new TransferSpeedColumn(), // Added speed metric
    })
    .StartAsync(async ctx => 
    {
        // Define tasks in UI
        var masterTask = ctx.AddTask("Total Progress");
        var segmentTasks = new List<ProgressTask>();

        // Wait for segments to be initialized
        while (true)
        {
            var taskState = downloadService.GetDownload(downloadId);
            if (taskState?.Segments.Count > 0)
            {
                foreach (var seg in taskState.Segments)
                {
                    segmentTasks.Add(ctx.AddTask($"Segment {seg.Index + 1}", new ProgressTaskSettings { MaxValue = seg.TotalBytes }));
                }
                break;
            }
            await Task.Delay(50);
        }

        // Input Polling Task
        var inputTask = Task.Run(async () => 
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.P)
                    {
                        await downloadService.PauseDownloadAsync(downloadId);
                        masterTask.Description = "[yellow]Paused (Press R to Resume)[/]";
                    }
                    else if (key.Key == ConsoleKey.R)
                    {
                        await downloadService.ResumeDownloadAsync(downloadId);
                        masterTask.Description = "Downloading...";
                    }
                }
                await Task.Delay(100);
            }
        });

        // UI Update Loop
        while(true)
        {
            var taskState = downloadService.GetDownload(downloadId);
            if (taskState == null) break;

            // Update Master
            masterTask.MaxValue = taskState.TotalBytes > 0 ? taskState.TotalBytes : 100;
            masterTask.Value = taskState.BytesDownloaded;

            // Update Segments
            for (int i = 0; i < taskState.Segments.Count; i++)
            {
                var seg = taskState.Segments[i];
                if (i < segmentTasks.Count)
                {
                    segmentTasks[i].Value = seg.BytesDownloaded;
                    if (seg.Status == DownloadStatus.Completed)
                    {
                         segmentTasks[i].Description = $"[green]Segment {seg.Index + 1} Done[/]";
                    }
                }
            }

            if (taskState.Status == DownloadStatus.Completed)
            {
                masterTask.Value = masterTask.MaxValue;
                masterTask.Description = "[green]Download Complete![/]";
                break;
            }
            
            if (taskState.Status == DownloadStatus.Failed)
            {
                masterTask.Description = $"[red]Failed: {taskState.ErrorMessage}[/]";
                break;
            }

            await Task.Delay(100);
        }
    });

// -------------------------
