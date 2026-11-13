namespace DotNetDownloadManager.UI;

public enum SlashCommandKind { Add, Pause, Resume, Cancel, Clear, Help, Quit }

/// <param name="Name">Name without the slash, e.g. "pause".</param>
/// <param name="Usage">How to type it, e.g. "/add &lt;url&gt;".</param>
public sealed record SlashCommand(SlashCommandKind Kind, string Name, string Usage, string Description);

/// <summary>The slash commands typed into the input box, with prefix autocomplete.</summary>
public static class SlashCommands
{
    public static IReadOnlyList<SlashCommand> All { get; } = new[]
    {
        new SlashCommand(SlashCommandKind.Add, "add", "/add <url>", "Download a URL (pasting it works too)"),
        new SlashCommand(SlashCommandKind.Pause, "pause", "/pause", "Pause the selected download"),
        new SlashCommand(SlashCommandKind.Resume, "resume", "/resume", "Resume or retry the selected download"),
        new SlashCommand(SlashCommandKind.Cancel, "cancel", "/cancel", "Cancel the selected download and delete its partial file"),
        new SlashCommand(SlashCommandKind.Clear, "clear", "/clear", "Remove finished downloads from the list (files are kept)"),
        new SlashCommand(SlashCommandKind.Help, "help", "/help", "Show keys and commands"),
        new SlashCommand(SlashCommandKind.Quit, "quit", "/quit", "Save progress and exit"),
    };

    /// <summary>Commands whose name starts with what was typed after "/" (empty unless the input is "/" + a partial name).</summary>
    public static IReadOnlyList<SlashCommand> Suggest(string input)
    {
        if (!input.StartsWith('/') || input.Contains(' ')) return Array.Empty<SlashCommand>();
        var prefix = input[1..];
        return All.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Parses "/name args". Accepts unambiguous prefixes ("/pa" → /pause).</summary>
    public static bool TryParse(string input, out SlashCommand? command, out string arguments)
    {
        command = null;
        arguments = string.Empty;

        var text = input.Trim();
        if (!text.StartsWith('/')) return false;

        var space = text.IndexOf(' ');
        var name = space < 0 ? text[1..] : text[1..space];
        if (name.Length == 0) return false;

        var exact = All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var matches = exact is not null
            ? new List<SlashCommand> { exact }
            : All.Where(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) return false;

        command = matches[0];
        arguments = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        return true;
    }
}
