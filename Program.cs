using Spectre.Console;
using DotNetDownloadManager.Infrastructure.Network;
using DotNetDownloadManager.Infrastructure.Storage;
using DotNetDownloadManager.Services;
using DotNetDownloadManager.Core.Models;
using Spectre.Console.Rendering;

// --- Initialize Services ---
var network = new HttpNetworkClient();
var storage = new LocalFileStorage();
var downloadService = new DownloadService(network, storage);

// Create the main dashboard layout
var layout = new Layout("Root")
    .SplitRows(
        new Layout("Header").Size(10),
        new Layout("Body"),
        new Layout("Footer").Size(3));

layout["Body"].SplitColumns(
    new Layout("Left"),
    new Layout("Right"));

// Track current download
Guid? currentDownloadId = null;
bool shouldExit = false;
bool needsInput = false;

// Initialize layout with content BEFORE starting Live display
layout["Header"].Update(BuildHeader());
layout["Left"].Update(BuildCurrentDownloadPanel(downloadService, currentDownloadId));
layout["Right"].Update(BuildHistoryPanel(downloadService));
layout["Footer"].Update(BuildFooter(currentDownloadId));

while (!shouldExit)
{
    await AnsiConsole.Live(layout)
        .AutoClear(false)
        .StartAsync(async ctx =>
        {
            while (!shouldExit && !needsInput)
            {
                // Update Header
                layout["Header"].Update(BuildHeader());

                // Update Left Panel - Current Download
                layout["Left"].Update(BuildCurrentDownloadPanel(downloadService, currentDownloadId));

                // Update Right Panel - History
                layout["Right"].Update(BuildHistoryPanel(downloadService));

                // Update Footer - Input/Controls
                layout["Footer"].Update(BuildFooter(currentDownloadId));

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
            
            // Wait for segments to initialize
            await Task.Delay(200);
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLine("[red]Invalid URL provided.[/]");
            await Task.Delay(2000);
        }
    }
}

static Panel BuildHeader()
{
    var title = new FigletText("DotNet Download Manager")
    {
        Color = Color.Blue
    };

    var grid = new Grid();
    grid.AddColumn(new GridColumn().NoWrap());
    grid.AddRow(title);
    grid.AddRow(new Markup("[dim]Press [green]N[/]=New Download | [yellow]P[/]=Pause | [blue]R[/]=Resume | [red]C[/]=Cancel | [grey]Q[/]=Quit[/]"));

    return new Panel(grid)
    {
        Border = BoxBorder.Double,
        BorderStyle = new Style(Color.Blue)
    };
}

static Panel BuildCurrentDownloadPanel(DownloadService downloadService, Guid? downloadId)
{
    if (!downloadId.HasValue)
    {
        var emptyTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders();
        emptyTable.AddColumn("");
        emptyTable.AddRow("[dim]No active download[/]");
        emptyTable.AddRow("");
        emptyTable.AddRow("[yellow]Press 'N' to start a new download[/]");

        return new Panel(emptyTable)
        {
            Header = new PanelHeader("[bold blue]Current Download[/]"),
            Border = BoxBorder.Rounded
        };
    }

    var task = downloadService.GetDownload(downloadId.Value);
    if (task == null)
    {
        var errorTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders();
        errorTable.AddColumn("");
        errorTable.AddRow("[red]Download not found[/]");

        return new Panel(errorTable)
        {
            Header = new PanelHeader("[bold blue]Current Download[/]"),
            Border = BoxBorder.Rounded
        };
    }

    return new Panel(BuildProgressView(task))
    {
        Header = new PanelHeader("[bold blue]Current Download[/]"),
        Border = BoxBorder.Rounded
    };
}

static Panel BuildHistoryPanel(DownloadService downloadService)
{
    return new Panel(BuildHistoryView(downloadService.GetAllDownloads()))
    {
        Header = new PanelHeader("[bold green]Download History[/]"),
        Border = BoxBorder.Rounded
    };
}

static Panel BuildFooter(Guid? currentDownloadId)
{
    var status = currentDownloadId.HasValue 
        ? "[green]Download in progress...[/]" 
        : "[yellow]Ready for new download[/]";

    return new Panel(new Markup($"{status} | [dim]Waiting for input...[/]"))
    {
        Border = BoxBorder.None
    };
}

static IRenderable BuildProgressView(DownloadTask task)
{
    var table = new Table()
        .Border(TableBorder.None)
        .HideHeaders();

    table.AddColumn(new TableColumn(""));

    // Status
    var statusColor = task.Status switch
    {
        DownloadStatus.Downloading => "green",
        DownloadStatus.Paused => "yellow",
        DownloadStatus.Completed => "green",
        DownloadStatus.Failed => "red",
        DownloadStatus.Cancelled => "red",
        _ => "white"
    };

    var statusText = task.Status switch
    {
        DownloadStatus.Paused => "⏸ Paused",
        DownloadStatus.Downloading => "⬇ Downloading...",
        DownloadStatus.Completed => "✓ Complete!",
        DownloadStatus.Failed => $"✗ Failed: {task.ErrorMessage}",
        DownloadStatus.Cancelled => "✗ Cancelled",
        _ => task.Status.ToString()
    };

    table.AddRow($"[bold]File:[/] {task.Filename}");
    table.AddRow($"[bold]Status:[/] [{statusColor}]{statusText}[/]");
    table.AddRow($"[bold]Progress:[/] {task.Progress:F2}% ({FormatBytes(task.BytesDownloaded)} / {FormatBytes(task.TotalBytes)})");
    table.AddEmptyRow();

    // Overall Progress Bar
    var overallBar = new BarChart()
        .Width(50)
        .Label("[bold]Total[/]")
        .CenterLabel();
    
    if (task.TotalBytes > 0)
    {
        overallBar.AddItem("", task.Progress, Color.Green);
    }

    table.AddRow(overallBar);
    table.AddEmptyRow();

    // Segment Progress (compact view)
    if (task.Segments != null && task.Segments.Any())
    {
        table.AddRow("[bold underline]Segments:[/]");
        
        var segmentGrid = new Grid();
        segmentGrid.AddColumn(new GridColumn().NoWrap().PadRight(2));
        segmentGrid.AddColumn(new GridColumn().NoWrap());
        
        for (int i = 0; i < task.Segments.Count; i += 2)
        {
            var seg1 = task.Segments[i];
            var seg1Progress = seg1.TotalBytes > 0 ? (double)seg1.BytesDownloaded / seg1.TotalBytes * 100 : 0;
            var seg1Icon = GetSegmentIcon(seg1.Status);
            var seg1Color = GetSegmentColor(seg1.Status);
            var seg1Text = $"[{seg1Color}]{seg1Icon} Seg {seg1.Index + 1}: {seg1Progress:F0}%[/]";
            
            if (i + 1 < task.Segments.Count)
            {
                var seg2 = task.Segments[i + 1];
                var seg2Progress = seg2.TotalBytes > 0 ? (double)seg2.BytesDownloaded / seg2.TotalBytes * 100 : 0;
                var seg2Icon = GetSegmentIcon(seg2.Status);
                var seg2Color = GetSegmentColor(seg2.Status);
                var seg2Text = $"[{seg2Color}]{seg2Icon} Seg {seg2.Index + 1}: {seg2Progress:F0}%[/]";
                
                segmentGrid.AddRow(new Markup(seg1Text), new Markup(seg2Text));
            }
            else
            {
                segmentGrid.AddRow(new Markup(seg1Text), new Markup(""));
            }
        }
        
        table.AddRow(segmentGrid);
    }
    else
    {
        table.AddRow("[dim]Initializing segments...[/]");
    }

    return table;
}

static string GetSegmentIcon(DownloadStatus status) => status switch
{
    DownloadStatus.Completed => "✓",
    DownloadStatus.Downloading => "⬇",
    DownloadStatus.Paused => "⏸",
    DownloadStatus.Cancelled => "✗",
    _ => "○"
};

static string GetSegmentColor(DownloadStatus status) => status switch
{
    DownloadStatus.Completed => "green",
    DownloadStatus.Downloading => "blue",
    DownloadStatus.Paused => "yellow",
    DownloadStatus.Cancelled => "red",
    _ => "grey"
};

static IRenderable BuildHistoryView(IEnumerable<DownloadTask> allDownloads)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey);

    table.AddColumn(new TableColumn("[bold]Filename[/]").LeftAligned());
    table.AddColumn(new TableColumn("[bold]Status[/]").Centered());
    table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

    if (allDownloads == null || !allDownloads.Any())
    {
        table.AddRow("[dim]No downloads yet[/]", "", "");
        return table;
    }

    var downloads = allDownloads.OrderByDescending(d => d.CreatedAt).Take(10);
    
    foreach (var download in downloads)
    {
        var statusIcon = download.Status switch
        {
            DownloadStatus.Completed => "[green]✓[/]",
            DownloadStatus.Downloading => "[blue]⬇[/]",
            DownloadStatus.Paused => "[yellow]⏸[/]",
            DownloadStatus.Failed => "[red]✗[/]",
            DownloadStatus.Cancelled => "[red]✗[/]",
            _ => "[grey]○[/]"
        };

        var statusText = download.Status switch
        {
            DownloadStatus.Completed => $"{statusIcon} Done",
            DownloadStatus.Downloading => $"{statusIcon} {download.Progress:F0}%",
            DownloadStatus.Paused => $"{statusIcon} Paused",
            DownloadStatus.Failed => $"{statusIcon} Failed",
            DownloadStatus.Cancelled => $"{statusIcon} Cancelled",
            _ => statusIcon
        };

        table.AddRow(
            download.Filename.Length > 25 
                ? download.Filename.Substring(0, 22) + "..." 
                : download.Filename,
            statusText,
            FormatBytes(download.TotalBytes)
        );
    }

    return table;
}

static string FormatBytes(long bytes)
{
    string[] sizes = { "B", "KB", "MB", "GB", "TB" };
    double len = bytes;
    int order = 0;
    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len = len / 1024;
    }
    return $"{len:0.##} {sizes[order]}";
}
