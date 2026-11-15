using Microsoft.EntityFrameworkCore;
using Sleipnir.Core.Models;
using Sleipnir.Server.Identity;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Tests.Server;

public class IdentityServiceTests : IDisposable
{
    private readonly SleipnirDbFactory _factory = Database.OpenMemory();
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly IdentityService _identity;

    public IdentityServiceTests()
    {
        Database.MigrateAsync(_factory).GetAwaiter().GetResult();
        _identity = new IdentityService(_factory, () => _now);
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<string> CodeOf(Task task)
    {
        var e = await Assert.ThrowsAsync<IdentityException>(() => task);
        return e.Code;
    }

    [Fact]
    public async Task The_first_start_creates_an_admin_and_the_local_device()
    {
        var principal = await _identity.EnsureLocalAdminAsync("alice", "local-token");

        Assert.Equal("alice", principal.UserName);
        Assert.Equal(UserRole.Admin, principal.Role);
        Assert.True(principal.IsLocal);
        Assert.Equal("This computer", principal.DeviceName);
        Assert.Equal(principal, await _identity.AuthenticateAsync("local-token"));
        Assert.Single(await _identity.ListUsersAsync());
        Assert.Single(await _identity.ListDevicesAsync(null));
    }

    [Fact]
    public async Task A_regenerated_local_token_replaces_the_old_one_without_new_users()
    {
        var first = await _identity.EnsureLocalAdminAsync("alice", "old-token");

        var second = await _identity.EnsureLocalAdminAsync("alice", "new-token");

        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Null(await _identity.AuthenticateAsync("old-token"));
        Assert.NotNull(await _identity.AuthenticateAsync("new-token"));
        Assert.Single(await _identity.ListUsersAsync());
        Assert.Single(await _identity.ListDevicesAsync(null));
    }

    [Fact]
    public async Task Unknown_empty_and_revoked_tokens_do_not_authenticate()
    {
        await _identity.EnsureLocalAdminAsync("alice", "local-token");
        var user = await _identity.CreateUserAsync("bob", UserRole.Member);
        var issued = await _identity.IssueDeviceAsync(user.Id, "phone", "android");

        Assert.NotNull(await _identity.AuthenticateAsync(issued.Token));
        Assert.Null(await _identity.AuthenticateAsync("nope"));
        Assert.Null(await _identity.AuthenticateAsync(""));
        Assert.True(await _identity.RevokeDeviceAsync(issued.Device.Id));
        Assert.Null(await _identity.AuthenticateAsync(issued.Token));
        Assert.False(await _identity.RevokeDeviceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Issued_tokens_are_43_character_base64url_and_unique()
    {
        var user = await _identity.CreateUserAsync("bob", UserRole.Member);

        var a = await _identity.IssueDeviceAsync(user.Id, "phone", "android");
        var b = await _identity.IssueDeviceAsync(user.Id, "tablet", "android");

        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Token);
        Assert.NotEqual(a.Token, b.Token);
    }

    [Fact]
    public async Task Last_seen_is_updated_at_most_once_a_minute()
    {
        var user = await _identity.CreateUserAsync("bob", UserRole.Member);
        var issued = await _identity.IssueDeviceAsync(user.Id, "phone", "android");
        Assert.Null((await _identity.GetDeviceAsync(issued.Device.Id))!.LastSeenAt);

        await _identity.AuthenticateAsync(issued.Token);
        var first = (await _identity.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;
        _now = _now.AddSeconds(30);
        await _identity.AuthenticateAsync(issued.Token);
        var within = (await _identity.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;
        _now = _now.AddSeconds(31);
        await _identity.AuthenticateAsync(issued.Token);
        var after = (await _identity.GetDeviceAsync(issued.Device.Id))!.LastSeenAt;

        Assert.Equal(_now.UtcDateTime.AddSeconds(-61), first);
        Assert.Equal(first, within);
        Assert.Equal(_now.UtcDateTime, after);
    }

    [Fact]
    public async Task Only_the_hash_is_stored_never_the_token()
    {
        var user = await _identity.CreateUserAsync("bob", UserRole.Member);
        var issued = await _identity.IssueDeviceAsync(user.Id, "phone", "android");

        await using var db = await _factory.CreateDbContextAsync();
        var device = await db.Devices.SingleAsync();

        Assert.Equal(IdentityService.HashToken(issued.Token), device.TokenHash);
        Assert.Matches("^[0-9a-f]{64}$", device.TokenHash);
        Assert.DoesNotContain(issued.Token, string.Join('|', new[] { device.Name, device.Platform, device.TokenHash }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\u0001b")]
    [InlineData("a\0b")]
    public async Task Bad_names_are_rejected(string name)
    {
        Assert.Equal("invalid_name", await CodeOf(_identity.CreateUserAsync(name, UserRole.Member)));
    }

    [Fact]
    public async Task Names_are_limited_to_40_characters_trimmed_and_unique_ignoring_case()
    {
        Assert.Equal("invalid_name", await CodeOf(_identity.CreateUserAsync(new string('x', 41), UserRole.Member)));
        var created = await _identity.CreateUserAsync("  Bob  ", UserRole.Member);

        Assert.Equal("Bob", created.DisplayName);
        Assert.Equal("name_taken", await CodeOf(_identity.CreateUserAsync("bob", UserRole.Member)));
    }

    [Fact]
    public async Task The_only_admin_cannot_be_demoted_or_deleted()
    {
        var admin = (await _identity.EnsureLocalAdminAsync("alice", "t")).UserId;

        Assert.Equal("last_admin", await CodeOf(_identity.UpdateUserAsync(admin, null, UserRole.Member, null)));
        Assert.Equal("last_admin", await CodeOf(_identity.DeleteUserAsync(admin)));
        Assert.Equal(UserRole.Admin, (await _identity.GetUserAsync(admin))!.Role);
    }

    [Fact]
    public async Task With_two_admins_one_can_be_demoted()
    {
        var alice = (await _identity.EnsureLocalAdminAsync("alice", "t")).UserId;
        await _identity.CreateUserAsync("bob", UserRole.Admin);

        var updated = await _identity.UpdateUserAsync(alice, null, UserRole.Member, null);

        Assert.Equal(UserRole.Member, updated.Role);
    }

    [Fact]
    public async Task Two_concurrent_demotions_never_remove_the_last_admin()
    {
        for (var round = 0; round < 20; round++)
        {
            using var factory = Database.OpenMemory();
            await Database.MigrateAsync(factory);
            var identity = new IdentityService(factory);
            var a = (await identity.EnsureLocalAdminAsync("alice", "t")).UserId;
            var b = (await identity.CreateUserAsync("bob", UserRole.Admin)).Id;

            var results = await Task.WhenAll(
                Attempt(identity.UpdateUserAsync(a, null, UserRole.Member, null)),
                Attempt(identity.UpdateUserAsync(b, null, UserRole.Member, null)));

            Assert.Equal(1, results.Count(ok => ok));
            Assert.Equal(1, (await identity.ListUsersAsync()).Count(u => u.Role == UserRole.Admin));
        }

        static async Task<bool> Attempt(Task<UserEntity> task)
        {
            try { await task; return true; }
            catch (IdentityException) { return false; }
        }
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_devices_and_tokens()
    {
        await _identity.EnsureLocalAdminAsync("alice", "t");
        var bob = await _identity.CreateUserAsync("bob", UserRole.Member);
        var phone = await _identity.IssueDeviceAsync(bob.Id, "phone", "android");

        var removed = await _identity.DeleteUserAsync(bob.Id);

        Assert.Equal(new[] { phone.Device.Id }, removed.Select(d => d.Id));
        Assert.Null(await _identity.AuthenticateAsync(phone.Token));
        Assert.Null(await _identity.GetUserAsync(bob.Id));
        Assert.Equal("user_not_found", await CodeOf(_identity.DeleteUserAsync(bob.Id)));
    }

    [Theory]
    [InlineData("Al/ice")]
    [InlineData("..")]
    [InlineData("  ")]
    [InlineData("a\0b")]
    [InlineData("....")]
    public void Folder_names_are_safe(string name)
    {
        var folder = UserNames.ToFolderName(name);

        Assert.NotEmpty(folder);
        Assert.DoesNotContain('/', folder);
        Assert.DoesNotContain('\\', folder);
        Assert.DoesNotContain('\0', folder);
        Assert.NotEqual(".", folder);
        Assert.NotEqual("..", folder);
    }

    [Fact]
    public void Long_folder_names_are_cut_to_40_characters()
    {
        Assert.Equal(40, UserNames.ToFolderName(new string('x', 100)).Length);
    }
}
