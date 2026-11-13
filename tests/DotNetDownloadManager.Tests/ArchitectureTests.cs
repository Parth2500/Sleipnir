using System.Reflection;
using DotNetDownloadManager.Core.Models;

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
}
