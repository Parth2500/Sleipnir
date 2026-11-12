using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DotNetDownloadManager.UI;

/// <summary>
/// Builds the Spectre.Console renderables that make up the dashboard.
/// </summary>
public static class DashboardRenderer
{
    public static Panel BuildHeader()
    {
        var title = new FigletText("DotNet Download Manager")
        {
            Color = Color.Blue
        };

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddRow(title);
        grid.AddRow(new Markup("[dim]Press [green]N[/]=New Download | [yellow]P[/]=Pause | [blue]R[/]=Resume | [red]C[/]=Cancel | [aqua]↑/↓[/]=Select | [grey]Q[/]=Quit[/]"));

        return new Panel(grid)
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Blue)
        };
    }

    public static Panel BuildCurrentDownloadPanel(IDownloadService downloadService, Guid? downloadId, SpeedTracker? speeds = null)
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

        return new Panel(BuildProgressView(task, speeds))
        {
            Header = new PanelHeader("[bold blue]Current Download[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public static Panel BuildHistoryPanel(IDownloadService downloadService, SpeedTracker? speeds = null)
    {
        return new Panel(BuildHistoryView(downloadService.GetAllDownloads()))
        {
            Header = new PanelHeader("[bold green]Download History[/]"),
            Border = BoxBorder.Rounded
        };
    }

    public static Panel BuildFooter(IDownloadService downloadService, Guid? currentDownloadId, SpeedTracker? speeds = null)
    {
        var task = currentDownloadId.HasValue ? downloadService.GetDownload(currentDownloadId.Value) : null;
        var active = downloadService.GetAllDownloads().Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        var status = task is null
            ? "[yellow]Ready for new download[/]"
            : $"Selected: [bold]{Markup.Escape(task.Filename)}[/] ({task.Status})";

        var speed = speeds is null ? "" : $" | [dim]⬇ {FormatBytes((long)speeds.TotalBytesPerSecond)}/s[/]";

        return new Panel(new Markup($"{status} | [dim]{active} active[/]{speed}"))
        {
            Border = BoxBorder.None
        };
    }

    public static IRenderable BuildProgressView(DownloadTask task, SpeedTracker? speeds = null)
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
            DownloadStatus.Failed => $"✗ Failed: {Markup.Escape(task.ErrorMessage ?? "unknown error")}",
            DownloadStatus.Cancelled => "✗ Cancelled",
            _ => task.Status.ToString()
        };

        table.AddRow($"[bold]File:[/] {Markup.Escape(task.Filename)}");
        table.AddRow($"[bold]Status:[/] [{statusColor}]{statusText}[/]");
        table.AddRow(task.IsSizeKnown
            ? $"[bold]Progress:[/] {task.Progress:F2}% ({FormatBytes(task.BytesDownloaded)} / {FormatBytes(task.TotalBytes)})"
            : $"[bold]Progress:[/] {FormatBytes(task.BytesDownloaded)} [dim](size unknown)[/]");

        if (speeds is not null && task.Status == DownloadStatus.Downloading)
        {
            var eta = speeds.GetEta(task);
            table.AddRow($"[bold]Speed:[/] {FormatBytes((long)speeds.GetBytesPerSecond(task.Id))}/s   [bold]ETA[/] {(eta is { } e ? FormatDuration(e) : "--")}");
        }
        table.AddEmptyRow();

        // Overall Progress Bar
        var overallBar = new BarChart()
            .Width(50)
            .Label("[bold]Total[/]")
            .CenterLabel();

        // A BarChart with no items throws while rendering, so always add one (0% until the size is known).
        overallBar.AddItem("", task.Progress, Color.Green);

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
                var seg1Text = SegmentText(task.Segments[i]);

                if (i + 1 < task.Segments.Count)
                {
                    var seg2Text = SegmentText(task.Segments[i + 1]);

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

    public static string GetSegmentIcon(DownloadStatus status) => status switch
    {
        DownloadStatus.Completed => "✓",
        DownloadStatus.Downloading => "⬇",
        DownloadStatus.Paused => "⏸",
        DownloadStatus.Cancelled => "✗",
        _ => "○"
    };

    public static string GetSegmentColor(DownloadStatus status) => status switch
    {
        DownloadStatus.Completed => "green",
        DownloadStatus.Downloading => "blue",
        DownloadStatus.Paused => "yellow",
        DownloadStatus.Cancelled => "red",
        _ => "grey"
    };

    public static IRenderable BuildHistoryView(IEnumerable<DownloadTask> allDownloads)
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
                DownloadStatus.Downloading when download.IsSizeKnown => $"{statusIcon} {download.Progress:F0}%",
                DownloadStatus.Downloading => $"{statusIcon} {FormatBytes(download.BytesDownloaded)}",
                DownloadStatus.Queued => $"{statusIcon} Queued",
                DownloadStatus.Paused => $"{statusIcon} Paused",
                DownloadStatus.Failed => $"{statusIcon} Failed",
                DownloadStatus.Cancelled => $"{statusIcon} Cancelled",
                _ => statusIcon
            };

            table.AddRow(
                Markup.Escape(download.Filename.Length > 25
                    ? download.Filename.Substring(0, 22) + "..."
                    : download.Filename),
                statusText,
                download.IsSizeKnown ? FormatBytes(download.TotalBytes) : "?"
            );
        }

        return table;
    }

    private static string SegmentText(DownloadSegment segment)
    {
        var amount = segment.IsOpenEnded
            ? FormatBytes(segment.BytesDownloaded)
            : $"{(segment.TotalBytes > 0 ? (double)segment.BytesDownloaded / segment.TotalBytes * 100 : 100):F0}%";
        return $"[{GetSegmentColor(segment.Status)}]{GetSegmentIcon(segment.Status)} Seg {segment.Index + 1}: {amount}[/]";
    }

    /// <summary>Formats a duration as m:ss, or h:mm:ss when it is an hour or longer.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    public static string FormatBytes(long bytes)
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
}
