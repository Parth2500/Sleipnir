using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sleipnir.Server.Security;

namespace Sleipnir.Server.Persistence;

/// <summary>Opens Sleipnir's SQLite database (a private file, or a throwaway in-memory one for tests).</summary>
public static class Database
{
    public static SleipnirDbFactory OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            SecretFile.CreateNew(path, ""); // an empty file is a valid new SQLite database, created 0600
        }
        else if (!OperatingSystem.IsWindows())
        {
            var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(path) != ownerOnly) File.SetUnixFileMode(path, ownerOnly);
        }
        return new SleipnirDbFactory($"Data Source={path};Pooling=False", keepAlive: null);
    }

    public static SleipnirDbFactory OpenMemory()
    {
        var connectionString = $"Data Source=file:sleipnir-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open(); // the database lives as long as one connection stays open
        return new SleipnirDbFactory(connectionString, keepAlive);
    }

    public static async Task MigrateAsync(IDbContextFactory<SleipnirDb> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}

public sealed class SleipnirDbFactory : IDbContextFactory<SleipnirDb>, IDisposable
{
    private readonly DbContextOptions<SleipnirDb> _options;
    private readonly SqliteConnection? _keepAlive;

    internal SleipnirDbFactory(string connectionString, SqliteConnection? keepAlive)
    {
        _keepAlive = keepAlive;
        _options = new DbContextOptionsBuilder<SleipnirDb>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
    }

    public SleipnirDb CreateDbContext() => new(_options);

    public Task<SleipnirDb> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _keepAlive?.Dispose();
}
