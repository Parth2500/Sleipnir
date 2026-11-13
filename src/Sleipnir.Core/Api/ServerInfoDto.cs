namespace Sleipnir.Core.Api;

/// <summary>What <c>GET /api/v1/server</c> tells anyone who asks: nothing beyond who and which versions.</summary>
public sealed record ServerInfoDto(string Name, string Version, string[] ApiVersions);
