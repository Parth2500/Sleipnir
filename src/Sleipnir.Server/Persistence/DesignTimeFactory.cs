using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sleipnir.Server.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SleipnirDb>
{
    public SleipnirDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SleipnirDb>()
            .UseSqlite("Data Source=design-time.db")
            .UseSnakeCaseNamingConvention()
            .Options);
}
