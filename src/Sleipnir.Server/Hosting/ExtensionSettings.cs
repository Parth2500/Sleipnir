using System.Text.Json;

namespace Sleipnir.Server.Hosting;

/// <summary>Whose downloads wait for an admin's approval under Bifröst (spec §7.4).</summary>
public enum ApprovalRule
{
    /// <summary>Members' downloads wait; admins' start at once. The default.</summary>
    Members,

    /// <summary>Everyone's downloads wait, admins' included.</summary>
    Everyone,

    /// <summary>Nothing waits.</summary>
    None,
}

/// <summary>
/// Sleipnir's own settings as an extension, from <c>&lt;BIFROST_DATA_DIR&gt;/settings.json</c>:
/// <c>{ "approval": "members" }</c>. The admin edits the file on the host.
/// </summary>
public sealed record ExtensionSettings(ApprovalRule Approval = ApprovalRule.Members)
{
    public const string FileName = "settings.json";

    /// <summary>A missing file means the defaults. A broken one too, and <paramref name="warn"/> says why: the strict default is the safe one.</summary>
    public static ExtensionSettings Load(string dataDir, Action<string>? warn = null)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return new ExtensionSettings();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("the file must hold one JSON object");
            if (!document.RootElement.TryGetProperty("approval", out var approval)) return new ExtensionSettings();
            return approval.ValueKind == JsonValueKind.String ? approval.GetString() switch
            {
                "members" => new ExtensionSettings(ApprovalRule.Members),
                "everyone" => new ExtensionSettings(ApprovalRule.Everyone),
                "none" => new ExtensionSettings(ApprovalRule.None),
                _ => throw new JsonException("\"approval\" must be \"members\", \"everyone\" or \"none\""),
            } : throw new JsonException("\"approval\" must be \"members\", \"everyone\" or \"none\"");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Ignoring {path}: {e.Message}. Members' downloads wait for approval.");
            return new ExtensionSettings();
        }
    }

    public bool NeedsApproval(bool isAdmin) => Approval switch
    {
        ApprovalRule.Everyone => true,
        ApprovalRule.None => false,
        _ => !isAdmin,
    };
}
