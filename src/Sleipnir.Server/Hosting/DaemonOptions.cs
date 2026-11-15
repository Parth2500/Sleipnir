namespace Sleipnir.Server.Hosting;

/// <param name="Port">First loopback port to try alone; 0 means "any free port" (used by tests). Unused under Bifröst.</param>
/// <param name="DefaultDestination">Alone: the folder for downloads that don't name one; defaults to ~/Downloads.</param>
/// <param name="PerPersonRoot">
/// Under Bifröst: each person's downloads go to <c>&lt;PerPersonRoot&gt;/&lt;person id&gt;</c>, and a client may not
/// name a folder. An interim rule until Bifröst's folders (Bifröst design §8.9, step 6).
/// </param>
public sealed record DaemonOptions(int Port = 7766, string? DefaultDestination = null, string? PerPersonRoot = null)
{
    public string ResolvedDestination => DefaultDestination
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
