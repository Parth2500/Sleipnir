using Sleipnir.Core.Models;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Server.Identity;

/// <summary>Decides where each user's downloads may be saved (spec §7.5).</summary>
public sealed class DestinationPolicy(string downloadsRoot)
{
    private const int MaxLinkHops = 40;

    public string DefaultFor(UserEntity user) => user.Role == UserRole.Admin
        ? downloadsRoot
        : user.AllowedRoot ?? Path.Combine(downloadsRoot, UserNames.ToFolderName(user.DisplayName));

    /// <summary>
    /// Admins get what they asked for (or the downloads folder). A member's folder must lie inside their root
    /// once both have been resolved through symlinks; the returned folder is the resolved path, so what was
    /// checked is exactly what the engine writes to.
    /// </summary>
    public bool TryResolve(UserEntity user, string? requested, out string folder)
    {
        folder = "";
        var blank = string.IsNullOrWhiteSpace(requested);
        if (user.Role == UserRole.Admin)
        {
            folder = blank ? downloadsRoot : requested!;
            return true;
        }

        var root = Resolve(DefaultFor(user));
        if (root is null) return false;
        if (blank)
        {
            folder = root;
            return true;
        }

        // ".." is refused outright: collapsing it textually would disagree with the operating system
        // whenever a symlink comes before it.
        if (!Path.IsPathFullyQualified(requested!) || requested!.Contains('\0')
            || requested.Split('/', '\\').Contains("..")) return false;

        var target = Resolve(requested);
        if (target is null || !IsInside(root, target)) return false;
        folder = target;
        return true;
    }

    private static bool IsInside(string root, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The absolute path with every existing symlink followed; null for a loop or a dangling link.</summary>
    private static string? Resolve(string path, int hops = 0)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            var info = new DirectoryInfo(next);
            if (info.LinkTarget is not null)
            {
                if (hops >= MaxLinkHops) return null;
                var target = info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                if (target is null) return null;
                return Resolve(Path.Combine([target, .. parts[(i + 1)..]]), hops + 1);
            }
            current = next;
        }
        return current;
    }
}
