using Sleipnir.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sleipnir.UI;

/// <summary>
/// Builds the whole screen from <see cref="TuiController"/> state. Pure: state in, renderable out.
/// </summary>
/// <remarks>
/// Layout, top to bottom: header · download list (or help) · toast · command suggestions · input box · hints.
/// Every line is fitted to the terminal width, and the list scrolls so the selection stays visible.
/// Anything that comes from a URL or server goes through <see cref="Markup.Escape"/>.
/// </remarks>
public static class TuiRenderer
{
    // Palette: one accent colour, semantic colours for states, grey for secondary text.
    private const string Accent = "#e8875b";
    private const string Busy = "#6fa8dc";
    private const string Good = "#6cc070";
    private const string Warn = "#d7b65a";
    private const string Bad = "#e06c6c";
    private const string Dim = "grey62";
    private const string SelectedBg = "on grey19";

    private const int SegmentsPerLine = 4;

    public static IRenderable Render(TuiController ui, SpeedTracker speeds, int width, int height)
    {
        width = Math.Max(30, width);
        height = Math.Max(10, height);
        var lineWidth = width - 1; // never touch the last column (avoids terminal auto-wrap)

        var downloads = ui.Downloads;
        var footer = BuildFooter(ui, lineWidth, downloads.Count > 0);
        var header = BuildHeader(downloads, speeds);

        // The input box is one renderable but three lines tall (top border, text, bottom border).
        var footerHeight = footer.Count + 2;
        var bodyHeight = Math.Max(1, height - 1 /* safety */ - header.Count - footerHeight);
        var body = ui.ShowHelp
            ? BuildHelp(lineWidth)
            : downloads.Count == 0
                ? BuildEmptyState()
                : BuildList(ui, downloads, speeds, lineWidth, bodyHeight);

        var lines = new List<IRenderable>(header);
        lines.AddRange(body.Take(bodyHeight));
        // Push the input box to the bottom of the screen, like a chat prompt.
        for (var i = body.Count; i < bodyHeight; i++) lines.Add(Text.Empty);
        lines.AddRange(footer);
        return new Rows(lines);
    }

    private static List<IRenderable> BuildHeader(IReadOnlyList<DownloadTask> downloads, SpeedTracker speeds)
    {
        var active = downloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        var summary = active == 0 ? "idle" : $"{active} active";
        if (speeds.TotalBytesPerSecond > 0) summary += $" · {Format.Bytes((long)speeds.TotalBytesPerSecond)}/s";

        return new List<IRenderable>
        {
            new Markup($"[{Accent}]⬇[/] [bold]Sleipnir[/]  [{Dim}]{summary}[/]"),
            Text.Empty,
        };
    }

    private static List<IRenderable> BuildEmptyState() => new()
    {
        new Markup($"  [bold]No downloads yet.[/]"),
        new Markup($"  [{Dim}]Paste a URL below to start one. Sleipnir splits it into parallel connections and[/]"),
        new Markup($"  [{Dim}]picks up where it left off if it is interrupted, even after you quit.[/]"),
    };

    private static List<IRenderable> BuildList(
        TuiController ui, IReadOnlyList<DownloadTask> downloads, SpeedTracker speeds, int width, int height)
    {
        var selectedIndex = Math.Max(0, downloads.ToList().FindIndex(d => d.Id == ui.SelectedId));
        var expanded = ui.ExpandedId is { } id ? downloads.FirstOrDefault(d => d.Id == id) : null;
        var detailLines = expanded is null ? 0 : DetailLineCount(expanded);

        // Which rows fit? Reserve one line for the "n more" hint when scrolling.
        var needsScroll = downloads.Count + detailLines > height;
        var capacity = Math.Max(1, (needsScroll ? height - 1 : height) - detailLines);
        var start = needsScroll ? Math.Clamp(selectedIndex - capacity / 2, 0, Math.Max(0, downloads.Count - capacity)) : 0;
        var end = Math.Min(downloads.Count, start + capacity);

        var lines = new List<IRenderable>();
        for (var i = start; i < end; i++)
        {
            var download = downloads[i];
            lines.Add(BuildRow(download, download.Id == ui.SelectedId, speeds, width));
            if (download == expanded) lines.AddRange(BuildDetails(download, width));
        }

        if (needsScroll)
        {
            var above = start;
            var below = downloads.Count - end;
            var parts = new List<string>();
            if (above > 0) parts.Add($"↑ {above} more");
            if (below > 0) parts.Add($"↓ {below} more");
            lines.Add(new Markup($"   [{Dim}]{string.Join("  ·  ", parts)}[/]"));
        }

        return lines;
    }

    private static IRenderable BuildRow(DownloadTask task, bool selected, SpeedTracker speeds, int width)
    {
        var (icon, color) = task.Status switch
        {
            DownloadStatus.Downloading => ("↓", Busy),
            DownloadStatus.Queued => ("○", Dim),
            DownloadStatus.Paused => ("‖", Warn),
            DownloadStatus.Completed => ("✓", Good),
            DownloadStatus.Failed => ("✗", Bad),
            DownloadStatus.Cancelled => ("✗", Dim),
            _ => ("·", Dim),
        };

        // Columns: pointer(3) icon(2) name(*) gap(2) state(*) ; state = bar + pct + info, or a status text.
        const int prefix = 5;
        var stateWidth = Math.Clamp(width - prefix - 2 - 12, 10, 60);
        var nameWidth = width - prefix - 2 - stateWidth;

        var pointer = selected ? $"[{Accent}] › [/]" : "   ";
        var name = Markup.Escape(Format.Fit(task.Filename, nameWidth));
        var nameMarkup = selected ? $"[bold]{name}[/]" : name;
        var state = BuildState(task, speeds, stateWidth, color);

        var row = $"{pointer}[{color}]{icon}[/] {nameMarkup}  {state}";
        return new Markup(selected ? $"[{SelectedBg}]{row}[/]" : row);
    }

    /// <summary>The right-hand part of a row, exactly <paramref name="width"/> characters wide.</summary>
    private static string BuildState(DownloadTask task, SpeedTracker speeds, int width, string color)
    {
        string Plain(string text, string style) => $"[{style}]{Markup.Escape(Format.Fit(text, width))}[/]";

        switch (task.Status)
        {
            case DownloadStatus.Queued:
                return Plain("queued", Dim);
            case DownloadStatus.Paused:
                return Plain(task.IsSizeKnown && task.TotalBytes > 0 ? $"paused at {Percent(task)}%" : "paused", Warn);
            case DownloadStatus.Completed:
                return Plain($"done · {Format.Bytes(task.TotalBytes)}", Dim);
            case DownloadStatus.Cancelled:
                return Plain("cancelled", Dim);
            case DownloadStatus.Failed:
                return Plain($"failed: {task.ErrorMessage ?? "unknown error"}", Bad);
        }

        // Downloading
        var speed = speeds.GetBytesPerSecond(task.Id);
        var eta = speeds.GetEta(task);
        var info = speed > 0
            ? $"{Format.Bytes((long)speed)}/s" + (eta is { } e ? $" · {Format.Duration(e)}" : "")
            : "starting…";

        if (!task.IsSizeKnown || task.TotalBytes <= 0)
        {
            var received = task.IsSizeKnown ? "connecting…" : $"{Format.Bytes(task.BytesDownloaded)} · size unknown";
            return $"[{color}]{Markup.Escape(Format.Fit($"{received}  {info}", width))}[/]";
        }

        const int pctWidth = 4;
        var infoWidth = Math.Min(info.Length, Math.Max(0, width - pctWidth - 2 - 6));
        var barWidth = Math.Max(0, width - pctWidth - 2 - infoWidth - 1);
        var filled = (int)Math.Round(barWidth * Math.Clamp(task.Progress / 100, 0, 1));
        var bar = new string('█', filled) + $"[{Dim}]{new string('░', barWidth - filled)}[/]";
        var pct = $"{Percent(task)}%".PadLeft(pctWidth);
        return $"[{color}]{bar}[/] {pct}  [{Dim}]{Markup.Escape(Format.Fit(info, infoWidth))}[/]";
    }

    /// <summary>
    /// Whole percent, never rounded up, so 99.9% isn't shown as done. Integer maths also avoids
    /// floating-point artefacts: (double)580 / 1000 * 100 is 57.999….
    /// </summary>
    private static long Percent(DownloadTask task) =>
        task.TotalBytes > 0 ? Math.Clamp(task.BytesDownloaded * 100 / task.TotalBytes, 0, 100) : 0;

    private static int DetailLineCount(DownloadTask task) =>
        3 + (task.Segments.Count + SegmentsPerLine - 1) / SegmentsPerLine + (task.ErrorMessage is null ? 0 : 1) + 1;

    private static List<IRenderable> BuildDetails(DownloadTask task, int width)
    {
        const string indent = "       ";
        var valueWidth = Math.Max(10, width - indent.Length - 10);
        IRenderable Line(string label, string value, string style = "default") =>
            new Markup($"{indent}[{Dim}]{label,-9}[/] [{style}]{Markup.Escape(Format.Fit(value, valueWidth).TrimEnd())}[/]");

        var size = task.IsSizeKnown && task.TotalBytes > 0
            ? $"{Format.Bytes(task.BytesDownloaded)} of {Format.Bytes(task.TotalBytes)} ({task.Progress:0.0}%)"
            : $"{Format.Bytes(task.BytesDownloaded)} received, size unknown";
        var connections = task.Segments.Count switch
        {
            0 => "",
            1 => " · 1 connection",
            var n => $" · {n} connections",
        };

        var lines = new List<IRenderable>
        {
            Line("URL", task.Url),
            Line("Saved to", task.DestinationPath),
            Line("Progress", size + connections),
        };

        for (var i = 0; i < task.Segments.Count; i += SegmentsPerLine)
        {
            var cells = task.Segments.Skip(i).Take(SegmentsPerLine).Select(s =>
            {
                var amount = s.Status == DownloadStatus.Completed ? "✓"
                    : s.Status == DownloadStatus.Queued ? "waiting" // between retries, e.g. after HTTP 429
                    : s.IsOpenEnded ? Format.Bytes(s.BytesDownloaded)
                    : $"{(s.TotalBytes > 0 ? s.BytesDownloaded * 100 / s.TotalBytes : 100)}%";
                return $"Seg {s.Index + 1} {amount}".PadRight(13) + "  "; // always a gap, even for the longest cell
            });
            var label = i == 0 ? "Segments" : "";
            lines.Add(new Markup($"{indent}[{Dim}]{label,-9}[/] {Markup.Escape(string.Concat(cells).TrimEnd())}"));
        }

        if (task.ErrorMessage is { } error) lines.Add(Line("Error", error, Bad));
        lines.Add(Text.Empty);
        return lines;
    }

    private static List<IRenderable> BuildHelp(int width)
    {
        (string Key, string What)[] keys =
        {
            ("Paste a URL, Enter", "Start a download (paste several to add them all)"),
            ("↑ ↓", "Select a download"),
            ("Space", "Pause or resume it"),
            ("Enter", "Show or hide its details"),
            ("Del, Del", "Cancel it and delete the partial file"),
            ("Esc", "Clear the input or close details"),
            ("Ctrl+C, Ctrl+C", "Quit. Running downloads resume next time"),
            ("?", "This help"),
        };

        var lines = new List<IRenderable> { new Markup("  [bold]Keys[/]  [" + Dim + "](when the input box is empty)[/]") };
        lines.AddRange(keys.Select(k => (IRenderable)new Markup($"    [{Accent}]{Markup.Escape(k.Key),-20}[/] {Markup.Escape(k.What)}")));
        lines.Add(Text.Empty);
        lines.Add(new Markup("  [bold]Commands[/]  [" + Dim + "](type / to see them as you type)[/]"));
        lines.AddRange(SlashCommands.All.Select(c => (IRenderable)new Markup($"    [{Accent}]{Markup.Escape(c.Usage),-20}[/] {Markup.Escape(c.Description)}")));
        return lines;
    }

    private static List<IRenderable> BuildFooter(TuiController ui, int width, bool hasDownloads)
    {
        var lines = new List<IRenderable>();

        if (ui.Toast is { } toast)
        {
            var (mark, style) = toast.Kind == ToastKind.Error ? ("✗", Bad) : ("•", Good);
            lines.Add(new Markup($" [{style}]{mark} {Markup.Escape(Format.Fit(toast.Message, width - 4).TrimEnd())}[/]"));
        }

        var suggestions = ui.Suggestions;
        for (var i = 0; i < suggestions.Count; i++)
        {
            var command = suggestions[i];
            var selected = i == ui.SuggestionIndex;
            var usage = Markup.Escape(command.Usage.PadRight(14));
            var description = Markup.Escape(Format.Fit(command.Description, Math.Max(0, width - 20)).TrimEnd());
            lines.Add(new Markup(selected
                ? $"[{SelectedBg}] [{Accent}]›[/] [bold]{usage}[/] {description} [/]"
                : $"   [{Dim}]{usage} {description}[/]"));
        }

        lines.Add(new Panel(new Markup(BuildInput(ui.Input, width - 6)))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey42),
            Padding = new Padding(1, 0),
            Expand = true,
        });

        string hint;
        if (ui.ShowHelp) hint = "Press any key to close help";
        else if (suggestions.Count > 0) hint = "↑↓ choose · Tab complete · Enter run · Esc cancel";
        else if (!ui.Input.IsEmpty) hint = ui.Input.Text.StartsWith('/') ? "Enter run · Esc clear" : "Enter add · Esc clear · / for commands";
        else if (hasDownloads) hint = "↑↓ select · Space pause/resume · Enter details · Del cancel · ? for help";
        else hint = "Paste a URL to start · / for commands · ? for help";
        lines.Add(new Markup($"  [{Dim}]{Markup.Escape(Format.Fit(hint, width - 2).TrimEnd())}[/]"));

        return lines;
    }

    /// <summary>The prompt line with a block cursor, scrolled horizontally to keep the cursor visible.</summary>
    private static string BuildInput(LineEditor input, int width)
    {
        var prompt = $"[{Accent}]>[/] ";
        if (input.IsEmpty)
        {
            return prompt + $"[invert] [/][{Dim}]Paste a URL, or type / for commands[/]";
        }

        var text = input.Text;
        var cursor = input.Cursor;
        var visible = Math.Max(5, width - 3);
        var offset = Math.Max(0, cursor - visible + 1);
        var window = text[offset..Math.Min(text.Length, offset + visible)];
        var at = cursor - offset;

        var before = Markup.Escape(window[..at]);
        var under = at < window.Length ? Markup.Escape(window[at].ToString()) : " ";
        var after = at < window.Length ? Markup.Escape(window[(at + 1)..]) : "";
        var lead = offset > 0 ? $"[{Dim}]…[/]" : "";
        return $"{prompt}{lead}{before}[invert]{under}[/]{after}";
    }
}
