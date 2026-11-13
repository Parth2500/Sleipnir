namespace Sleipnir.UI;

/// <summary>Human-friendly formatting shared by the UI.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>m:ss, or h:mm:ss when an hour or longer.</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    /// <summary>Cuts <paramref name="text"/> to <paramref name="width"/> characters, ending in "…" if shortened, and pads it.</summary>
    public static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (text.Length <= width) return text.PadRight(width);
        return width == 1 ? "…" : text[..(width - 1)] + "…";
    }
}
