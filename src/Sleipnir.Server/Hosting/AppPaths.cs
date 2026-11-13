namespace Sleipnir.Server.Hosting;

public enum PlatformKind { Linux, Windows, MacOS }

/// <summary>Where Sleipnir keeps its settings, data and runtime files (spec §5).</summary>
public sealed record AppPaths(string Settings, string Data, string Runtime)
{
    private const string AppFolder = "dnm";

    public static AppPaths ForCurrentUser()
    {
        var platform = OperatingSystem.IsWindows() ? PlatformKind.Windows
            : OperatingSystem.IsMacOS() ? PlatformKind.MacOS
            : PlatformKind.Linux;
        return Resolve(platform, Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static AppPaths Resolve(PlatformKind platform, Func<string, string?> env, string home)
    {
        string? Get(string name) => string.IsNullOrEmpty(env(name)) ? null : env(name);

        switch (platform)
        {
            case PlatformKind.Windows:
            {
                var roaming = Get("APPDATA") ?? $@"{home}\AppData\Roaming";
                var local = Get("LOCALAPPDATA") ?? $@"{home}\AppData\Local";
                return new AppPaths($@"{roaming}\{AppFolder}", $@"{local}\{AppFolder}", $@"{local}\{AppFolder}\run");
            }
            case PlatformKind.MacOS:
            {
                var support = $"{home}/Library/Application Support/{AppFolder}";
                return new AppPaths(support, support, support + "/run");
            }
            default:
            {
                var config = Get("XDG_CONFIG_HOME") ?? $"{home}/.config";
                var data = Get("XDG_DATA_HOME") ?? $"{home}/.local/share";
                var runtime = Get("XDG_RUNTIME_DIR") ?? Get("XDG_STATE_HOME") ?? $"{home}/.local/state";
                return new AppPaths($"{config}/{AppFolder}", $"{data}/{AppFolder}", $"{runtime}/{AppFolder}");
            }
        }
    }
}
