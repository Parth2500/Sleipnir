using Sleipnir.Core.Models;

namespace Sleipnir.Core.Api;

public sealed record UserDto(Guid Id, string DisplayName, UserRole Role, DateTime CreatedAt, string? AllowedRoot);

public sealed record DeviceDto(
    Guid Id, Guid UserId, string Name, string Platform, DateTime CreatedAt, DateTime? LastSeenAt, bool Revoked, bool IsLocal);

public sealed record CreateUserRequest(string? DisplayName, UserRole? Role);

public sealed record UpdateUserRequest(string? DisplayName, UserRole? Role, string? AllowedRoot);
