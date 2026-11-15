namespace Sleipnir.Server.Hosting;

/// <summary>
/// What Bifröst tells an extension it starts (Bifröst design §8.4; the contract is in
/// <c>docs/superpowers/plans/2026-11-15-two-modes.md</c>). Sleipnir runs as an extension only when
/// all of it is there.
/// </summary>
public sealed record ExtensionEnvironment(string Socket, string? ApiSocket, string DataDir, string Name)
{
    public const string InterfaceVersion = "1";

    public static ExtensionEnvironment? Read(Func<string, string?> env, out string? missing)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? Get(string name) => env(name) is { Length: > 0 } value ? value : null;

        missing = null;
        foreach (var name in new[] { "BIFROST_SOCKET", "BIFROST_DATA_DIR", "BIFROST_EXTENSION" })
        {
            if (Get(name) is null)
            {
                missing = name;
                return null;
            }
        }
        if (Get("BIFROST_INTERFACE") != InterfaceVersion)
        {
            missing = "BIFROST_INTERFACE";
            return null;
        }
        return new ExtensionEnvironment(Get("BIFROST_SOCKET")!, Get("BIFROST_API_SOCKET"), Get("BIFROST_DATA_DIR")!, Get("BIFROST_EXTENSION")!);
    }
}
