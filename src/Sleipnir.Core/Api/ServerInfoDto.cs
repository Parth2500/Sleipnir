namespace Sleipnir.Core.Api;

/// <summary>What <c>GET /api/v1/server</c> tells anyone who asks: who, which versions, and which mode.</summary>
/// <param name="Mode"><c>alone</c> (one machine, the person at it) or <c>extension</c> (under Bifröst).</param>
public sealed record ServerInfoDto(string Name, string Version, string[] ApiVersions, string Mode);
