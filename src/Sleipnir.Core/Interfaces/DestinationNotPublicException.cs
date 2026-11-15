namespace Sleipnir.Core.Interfaces;

/// <summary>
/// A download that may reach public addresses only (a member's, under Bifröst) tried to connect to the
/// home network, the machine itself, or another special address (spec §9, SSRF). Retrying won't help.
/// </summary>
public sealed class DestinationNotPublicException(string host)
    : Exception($"{host} is not a public address. Only an admin may download from the home network.")
{
    public string Host { get; } = host;
}
