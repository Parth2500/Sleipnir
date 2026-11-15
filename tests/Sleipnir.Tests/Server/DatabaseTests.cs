using Microsoft.EntityFrameworkCore;
using Sleipnir.Core.Models;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Tests.Server;

public class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleipnir-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static UserEntity User(string name, UserRole role = UserRole.Member) =>
        new() { Id = Guid.NewGuid(), DisplayName = name, Role = role, CreatedAt = DateTime.UtcNow };

    private static DeviceEntity Device(Guid userId, string hash = "hash") =>
        new() { Id = Guid.NewGuid(), UserId = userId, Name = "phone", Platform = "android", TokenHash = hash, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task A_new_database_file_is_private_and_has_the_tables()
    {
        var path = Path.Combine(_dir, "dnm.db");
        using var factory = Database.OpenFile(path);

        await Database.MigrateAsync(factory);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        await using var db = await factory.CreateDbContextAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();
        Assert.Contains("users", tables);
        Assert.Contains("devices", tables);
    }

    [Fact]
    public async Task Migrating_twice_is_harmless_and_keeps_data()
    {
        using var factory = Database.OpenFile(Path.Combine(_dir, "dnm.db"));
        await Database.MigrateAsync(factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(User("alice"));
            await db.SaveChangesAsync();
        }

        await Database.MigrateAsync(factory);

        await using var again = await factory.CreateDbContextAsync();
        Assert.Equal(1, await again.Users.CountAsync());
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        using var factory = Database.OpenMemory();
        await Database.MigrateAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        db.Users.Add(User("Bob"));
        await db.SaveChangesAsync();

        db.Users.Add(User("bob"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Token_hashes_are_unique()
    {
        using var factory = Database.OpenMemory();
        await Database.MigrateAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var user = User("alice");
        db.Users.Add(user);
        db.Devices.Add(Device(user.Id, "same"));
        await db.SaveChangesAsync();

        db.Devices.Add(Device(user.Id, "same"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_devices()
    {
        using var factory = Database.OpenMemory();
        await Database.MigrateAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var user = User("alice");
        db.Users.Add(user);
        db.Devices.Add(Device(user.Id));
        await db.SaveChangesAsync();

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task Roles_are_stored_as_lower_case_text()
    {
        using var factory = Database.OpenMemory();
        await Database.MigrateAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        db.Users.Add(User("alice", UserRole.Admin));
        await db.SaveChangesAsync();

        var raw = await db.Database.SqlQueryRaw<string>("SELECT role AS Value FROM users").SingleAsync();

        Assert.Equal("admin", raw);
        Assert.Equal(UserRole.Admin, (await db.Users.SingleAsync()).Role);
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_fails_to_migrate()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "dnm.db");
        await File.WriteAllTextAsync(path, "this is definitely not an sqlite database, just some text padding it out");
        using var factory = Database.OpenFile(path);

        await Assert.ThrowsAnyAsync<Exception>(() => Database.MigrateAsync(factory));
    }
}
