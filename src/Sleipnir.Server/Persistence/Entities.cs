using Sleipnir.Core.Models;

namespace Sleipnir.Server.Persistence;

public class UserEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Folder this user's downloads must stay inside; null means the default (see DestinationPolicy).</summary>
    public string? AllowedRoot { get; set; }
}

public class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";

    /// <summary>Hex SHA-256 of the device token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>The built-in "This computer" device whose token is the local admin token file.</summary>
    public bool IsLocal { get; set; }
}
