namespace Sleipnir.Server.Identity;

public static class UserNames
{
    public const int MaxLength = 40;

    /// <summary>Returns the trimmed name, or throws <c>invalid_name</c>.</summary>
    public static string Validate(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxLength || trimmed.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw new IdentityException("invalid_name");
        }
        return trimmed;
    }

    /// <summary>A name made safe to use as one folder name: no separators, never empty, <c>.</c> or <c>..</c>.</summary>
    public static string ToFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => char.IsControl(c) || c is '/' or '\\' || invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (safe.Length > MaxLength) safe = safe[..MaxLength].TrimEnd();
        return safe.Length == 0 ? "user" : safe;
    }
}
