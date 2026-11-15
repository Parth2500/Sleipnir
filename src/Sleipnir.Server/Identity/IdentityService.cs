using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sleipnir.Core.Models;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Server.Identity;

/// <summary>
/// Users, devices and token authentication. All access to the database goes through one gate: there is
/// a single daemon per user, so serializing is simple and makes "keep at least one admin" race-free.
/// </summary>
public sealed class IdentityService(IDbContextFactory<SleipnirDb> dbFactory, Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public Task<Principal> EnsureLocalAdminAsync(string adminName, string localToken) => With(async db =>
    {
        var admin = await db.Users.Where(u => u.Role == UserRole.Admin).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync();
        if (admin is null)
        {
            string name;
            try { name = UserNames.Validate(adminName); }
            catch (IdentityException) { name = "admin"; }
            if (await db.Users.AnyAsync(u => u.DisplayName == name)) name = "admin";
            admin = new UserEntity { Id = Guid.NewGuid(), DisplayName = name, Role = UserRole.Admin, CreatedAt = Now };
            db.Users.Add(admin);
        }

        var hash = HashToken(localToken);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.IsLocal);
        if (device is null)
        {
            device = new DeviceEntity
            {
                Id = Guid.NewGuid(), UserId = admin.Id, Name = "This computer", IsLocal = true,
                Platform = RuntimeInformation.OSDescription, CreatedAt = Now,
            };
            db.Devices.Add(device);
        }
        device.TokenHash = hash; // the token file may have been regenerated since last start
        device.RevokedAt = null;
        if (await db.Users.AnyAsync(u => u.Id == device.UserId && u.Role == UserRole.Admin) == false) device.UserId = admin.Id;

        await db.SaveChangesAsync();
        var owner = await db.Users.SingleAsync(u => u.Id == device.UserId);
        return new Principal(owner.Id, owner.DisplayName, owner.Role, device.Id, device.Name, true);
    });

    public Task<Principal?> AuthenticateAsync(string token) => With<Principal?>(async db =>
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAt == null);
        if (device is null) return null;
        var user = await db.Users.SingleAsync(u => u.Id == device.UserId);

        var now = Now;
        if (device.LastSeenAt is null || now - device.LastSeenAt >= LastSeenInterval)
        {
            device.LastSeenAt = now;
            await db.SaveChangesAsync();
        }
        return new Principal(user.Id, user.DisplayName, user.Role, device.Id, device.Name, device.IsLocal);
    });

    public Task<IssuedDevice> IssueDeviceAsync(Guid userId, string name, string platform) => With(async db =>
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId)) throw new IdentityException("user_not_found");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name, Platform = platform, TokenHash = HashToken(token), CreatedAt = Now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return new IssuedDevice(device, token);
    });

    public Task<bool> RevokeDeviceAsync(Guid deviceId) => With(async db =>
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId);
        if (device is null) return false;
        device.RevokedAt ??= Now;
        await db.SaveChangesAsync();
        return true;
    });

    public Task<UserEntity> CreateUserAsync(string displayName, UserRole role) => With(async db =>
    {
        var name = UserNames.Validate(displayName);
        if (await db.Users.AnyAsync(u => u.DisplayName == name)) throw new IdentityException("name_taken");
        var user = new UserEntity { Id = Guid.NewGuid(), DisplayName = name, Role = role, CreatedAt = Now };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    });

    public Task<UserEntity> UpdateUserAsync(Guid id, string? displayName, UserRole? role, string? allowedRoot) => With(async db =>
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw new IdentityException("user_not_found");
        if (displayName is not null)
        {
            var name = UserNames.Validate(displayName);
            if (await db.Users.AnyAsync(u => u.Id != id && u.DisplayName == name)) throw new IdentityException("name_taken");
            user.DisplayName = name;
        }
        if (role is { } newRole && newRole != user.Role)
        {
            if (user.Role == UserRole.Admin && await AdminCount(db) <= 1) throw new IdentityException("last_admin");
            user.Role = newRole;
        }
        if (allowedRoot is not null) user.AllowedRoot = string.IsNullOrWhiteSpace(allowedRoot) ? null : allowedRoot;
        await db.SaveChangesAsync();
        return user;
    });

    public Task<IReadOnlyList<DeviceEntity>> DeleteUserAsync(Guid id) => With<IReadOnlyList<DeviceEntity>>(async db =>
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw new IdentityException("user_not_found");
        if (user.Role == UserRole.Admin && await AdminCount(db) <= 1) throw new IdentityException("last_admin");
        var devices = await db.Devices.Where(d => d.UserId == id).ToListAsync();
        db.Users.Remove(user); // devices cascade
        await db.SaveChangesAsync();
        return devices;
    });

    public Task<UserEntity?> GetUserAsync(Guid id) => With<UserEntity?>(db => db.Users.FirstOrDefaultAsync(u => u.Id == id));

    public Task<IReadOnlyList<UserEntity>> ListUsersAsync() =>
        With<IReadOnlyList<UserEntity>>(async db => await db.Users.OrderBy(u => u.CreatedAt).ThenBy(u => u.DisplayName).ToListAsync());

    public Task<DeviceEntity?> GetDeviceAsync(Guid id) => With<DeviceEntity?>(db => db.Devices.FirstOrDefaultAsync(d => d.Id == id));

    public Task<IReadOnlyList<DeviceEntity>> ListDevicesAsync(Guid? userId) => With<IReadOnlyList<DeviceEntity>>(async db =>
        await db.Devices.Where(d => userId == null || d.UserId == userId).OrderBy(d => d.CreatedAt).ToListAsync());

    private DateTime Now => _clock().UtcDateTime;

    private static Task<int> AdminCount(SleipnirDb db) => db.Users.CountAsync(u => u.Role == UserRole.Admin);

    private async Task<T> With<T>(Func<SleipnirDb, Task<T>> work)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await work(db);
        }
        finally
        {
            _gate.Release();
        }
    }
}
