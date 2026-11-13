using Sleipnir.Infrastructure.Persistence;
using Sleipnir.Server.Hosting;

namespace Sleipnir.Tests.Server;

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Linux_without_xdg_variables_uses_home_defaults()
    {
        var paths = AppPaths.Resolve(PlatformKind.Linux, Env(), "/home/u");

        Assert.Equal("/home/u/.config/dnm", paths.Settings);
        Assert.Equal("/home/u/.local/share/dnm", paths.Data);
        Assert.Equal("/home/u/.local/state/dnm", paths.Runtime);
    }

    [Fact]
    public void Linux_honours_xdg_variables()
    {
        var paths = AppPaths.Resolve(PlatformKind.Linux,
            Env(("XDG_CONFIG_HOME", "/c"), ("XDG_DATA_HOME", "/d"), ("XDG_RUNTIME_DIR", "/r")), "/home/u");

        Assert.Equal("/c/dnm", paths.Settings);
        Assert.Equal("/d/dnm", paths.Data);
        Assert.Equal("/r/dnm", paths.Runtime);
    }

    [Fact]
    public void Linux_runtime_falls_back_to_xdg_state_home()
    {
        var paths = AppPaths.Resolve(PlatformKind.Linux, Env(("XDG_STATE_HOME", "/s")), "/home/u");

        Assert.Equal("/s/dnm", paths.Runtime);
    }

    [Fact]
    public void Empty_variables_count_as_unset()
    {
        var paths = AppPaths.Resolve(PlatformKind.Linux, Env(("XDG_DATA_HOME", "")), "/home/u");

        Assert.Equal("/home/u/.local/share/dnm", paths.Data);
    }

    [Fact]
    public void Windows_uses_appdata_and_localappdata()
    {
        var paths = AppPaths.Resolve(PlatformKind.Windows,
            Env(("APPDATA", @"C:\A"), ("LOCALAPPDATA", @"C:\L")), @"C:\Users\u");

        Assert.Equal(@"C:\A\dnm", paths.Settings);
        Assert.Equal(@"C:\L\dnm", paths.Data);
        Assert.Equal(@"C:\L\dnm\run", paths.Runtime);
    }

    [Fact]
    public void MacOS_uses_application_support()
    {
        var paths = AppPaths.Resolve(PlatformKind.MacOS, Env(), "/home/u");

        Assert.Equal("/home/u/Library/Application Support/dnm", paths.Settings);
        Assert.Equal("/home/u/Library/Application Support/dnm", paths.Data);
        Assert.Equal("/home/u/Library/Application Support/dnm/run", paths.Runtime);
    }

    [Fact]
    public void Linux_data_folder_matches_where_the_engine_already_keeps_downloads()
    {
        if (!OperatingSystem.IsLinux()) return;

        var paths = AppPaths.ForCurrentUser();

        Assert.Equal(JsonDownloadStateStore.DefaultDirectory(), Path.Combine(paths.Data, "downloads"));
    }
}
