using Sleipnir.Core.Models;

namespace Sleipnir.Core.Api;

public sealed record MeDto(Guid UserId, string UserName, UserRole Role, Guid DeviceId, string DeviceName);
