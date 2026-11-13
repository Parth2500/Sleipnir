using System.Web;

namespace Sleipnir.Services;

/// <summary>
/// Turns a URL into a safe local file name and avoids clobbering existing files.
/// </summary>
public static class FileNameResolver
{
    private const string DefaultName = "downloaded_file";

    private static readonly HashSet<char> InvalidChars =
        new(Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }));

    /// <summary>
    /// Picks a file name from <c>?filename=</c> (plus optional <c>&amp;ext=</c>) or the last path segment.
    /// The result never contains directory separators, so it cannot escape the output directory.
    /// </summary>
    public static string FromUrl(Uri uri)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        var fromQuery = query["filename"];

        string candidate;
        if (!string.IsNullOrWhiteSpace(fromQuery))
        {
            var ext = query["ext"];
            candidate = string.IsNullOrWhiteSpace(ext) ? fromQuery : $"{fromQuery}.{ext}";
        }
        else
        {
            candidate = uri.LocalPath;
        }

        return Sanitize(candidate);
    }

    /// <summary>Strips any directory part and replaces characters that are unsafe in file names.</summary>
    public static string Sanitize(string name)
    {
        // Treat both separator styles as separators regardless of OS, then keep only the last part.
        var lastPart = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var cleaned = new string(lastPart.Select(c => InvalidChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');

        return cleaned is "" or "." or ".." ? DefaultName : cleaned;
    }

    /// <summary>
    /// Returns <paramref name="fileName"/> in <paramref name="directory"/>, or "name (1).ext", "name (2).ext", …
    /// if <paramref name="isTaken"/> reports the path as already in use.
    /// </summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool> isTaken)
    {
        var path = Path.Combine(directory, fileName);
        if (!isTaken(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!isTaken(path)) return path;
        }
    }
}
