using System.Reflection;
using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.Infrastructure.Persistence;
using DotNetDownloadManager.Services;

namespace DotNetDownloadManager.Tests;

/// <summary>Pins the project layering so Core and Engine stay reusable (e.g. by a future Android app).</summary>
public class ArchitectureTests
{
    [Fact]
    public void Core_is_its_own_assembly_with_only_framework_references()
    {
        Assembly core = typeof(DownloadTask).Assembly;

        Assert.Equal("Sleipnir.Core", core.GetName().Name);
        Assert.All(core.GetReferencedAssemblies(), r =>
            Assert.True(r.Name is "netstandard" or "mscorlib" || r.Name!.StartsWith("System."),
                $"Core must not reference {r.Name}"));
    }

    [Fact]
    public void Engine_has_no_console_or_web_dependencies()
    {
        Assembly engine = typeof(DownloadService).Assembly;

        Assert.Equal("Sleipnir.Engine", engine.GetName().Name);
        var references = engine.GetReferencedAssemblies().Select(r => r.Name!).ToList();
        Assert.Contains("Sleipnir.Core", references);
        Assert.DoesNotContain(references,
            n => n.StartsWith("Spectre") || n.StartsWith("Microsoft.AspNetCore") || n == "dnm");
    }

    [Fact]
    public void Default_state_directory_is_unchanged()
    {
        string dir = JsonDownloadStateStore.DefaultDirectory().Replace('\\', '/');

        Assert.EndsWith("dnm/downloads", dir);
    }
}
