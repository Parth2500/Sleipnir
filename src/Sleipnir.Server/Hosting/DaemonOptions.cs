namespace Sleipnir.Server.Hosting;

/// <param name="AdminToken">The local admin bearer token.</param>
/// <param name="Port">First loopback port to try; 0 means "any free port" (used by tests).</param>
/// <param name="DefaultDestination">Folder for downloads that don't name one; defaults to ~/Downloads.</param>
public sealed record DaemonOptions(string AdminToken, int Port = 7766, string? DefaultDestination = null)
{
    public string ResolvedDestination => DefaultDestination
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
