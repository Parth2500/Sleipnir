using Sleipnir.Core.Models;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Server.Identity;

/// <summary>Who is calling: the user, the role, and the device whose token was presented.</summary>
public sealed record Principal(Guid UserId, string UserName, UserRole Role, Guid DeviceId, string DeviceName, bool IsLocal)
{
    public bool IsAdmin => Role == UserRole.Admin;
}

public sealed record IssuedDevice(DeviceEntity Device, string Token);

/// <summary>A refused identity operation; <see cref="Code"/> is the stable API error code.</summary>
public sealed class IdentityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
