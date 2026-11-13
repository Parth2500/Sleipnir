namespace Sleipnir.Server.Api;

internal static class PathRules
{
    /// <summary>
    /// Blank means "use the default". Anything else must be an absolute folder path: a relative path
    /// would resolve against whatever directory the daemon happens to run in, and "~" is not expanded.
    /// </summary>
    public static bool IsValidFolder(string? path) =>
        string.IsNullOrWhiteSpace(path) || (!path.Contains('\0') && Path.IsPathFullyQualified(path));
}
