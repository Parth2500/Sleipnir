using Sleipnir.Core.Models;
using Sleipnir.Server.Identity;
using Sleipnir.Server.Persistence;

namespace Sleipnir.Tests.Server;

public class DestinationPolicyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _downloads;
    private readonly string _root;
    private readonly DestinationPolicy _policy;
    private readonly UserEntity _alice;

    public DestinationPolicyTests()
    {
        _downloads = Path.Combine(_dir.Path, "Downloads");
        _root = Path.Combine(_downloads, "alice");
        Directory.CreateDirectory(_root);
        _policy = new DestinationPolicy(_downloads);
        _alice = new UserEntity { Id = Guid.NewGuid(), DisplayName = "alice", Role = UserRole.Member };
    }

    public void Dispose() => _dir.Dispose();

    private bool Allowed(string? requested, UserEntity? user = null) => _policy.TryResolve(user ?? _alice, requested, out _);

    [Fact]
    public void Members_default_to_their_own_folder_and_admins_to_the_downloads_folder()
    {
        var admin = new UserEntity { Id = Guid.NewGuid(), DisplayName = "root", Role = UserRole.Admin };

        Assert.Equal(_root, _policy.DefaultFor(_alice));
        Assert.Equal(_downloads, _policy.DefaultFor(admin));
        Assert.True(_policy.TryResolve(_alice, null, out var folder));
        Assert.Equal(_root, folder);
        Assert.True(_policy.TryResolve(admin, "  ", out var adminFolder));
        Assert.Equal(_downloads, adminFolder);
    }

    [Fact]
    public void A_custom_allowed_root_replaces_the_default()
    {
        var custom = Path.Combine(_dir.Path, "elsewhere");
        _alice.AllowedRoot = custom;

        Assert.Equal(custom, _policy.DefaultFor(_alice));
        Assert.True(Allowed(Path.Combine(custom, "sub")));
        Assert.False(Allowed(_root));
    }

    [Fact]
    public void The_root_and_everything_below_it_is_allowed()
    {
        Assert.True(Allowed(_root));
        Assert.True(Allowed(Path.Combine(_root, "movies")));
        Assert.True(Allowed(Path.Combine(_root, "a", "b", "c"))); // does not exist yet
    }

    [Fact]
    public void Dot_segments_and_trailing_separators_are_normalized()
    {
        Assert.True(_policy.TryResolve(_alice, _root + "/./sub/", out var folder));
        Assert.Equal(Path.Combine(_root, "sub"), folder);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../bob")]
    [InlineData("alice/../../bob")]
    [InlineData("sub/../other")]
    public void Climbing_out_of_the_root_or_using_dot_dot_at_all_is_refused(string relative)
    {
        Assert.False(Allowed(Path.Combine(_root, relative)));
    }

    [Fact]
    public void Other_places_and_look_alike_siblings_are_refused()
    {
        Directory.CreateDirectory(_root + "-evil");

        Assert.False(Allowed(_dir.Path));
        Assert.False(Allowed(_downloads));
        Assert.False(Allowed(_root + "-evil"));
        Assert.False(Allowed(Path.Combine(_root + "-evil", "x")));
        Assert.False(Allowed("/etc"));
    }

    [Fact]
    public void A_relative_request_is_refused()
    {
        Assert.False(Allowed("alice/movies"));
        Assert.False(Allowed("~/Downloads"));
    }

    [Fact]
    public void A_symlink_inside_the_root_that_points_outside_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_dir.Path, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);

        Assert.False(Allowed(Path.Combine(_root, "link")));
        Assert.False(Allowed(Path.Combine(_root, "link", "sub")));
        Assert.False(Allowed(Path.Combine(_root, "link", "..", "x")));
    }

    [Fact]
    public void A_symlink_inside_the_root_that_stays_inside_is_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), real);

        Assert.True(_policy.TryResolve(_alice, Path.Combine(_root, "alias", "sub"), out var folder));
        Assert.Equal(Path.Combine(real, "sub"), folder);
    }

    [Fact]
    public void A_root_that_is_itself_a_symlink_is_resolved_before_comparing()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_dir.Path, "disk2", "alice");
        Directory.CreateDirectory(target);
        Directory.Delete(_root);
        Directory.CreateSymbolicLink(_root, target);

        Assert.True(_policy.TryResolve(_alice, Path.Combine(_root, "movies"), out var folder));
        Assert.Equal(Path.Combine(target, "movies"), folder);
        Assert.False(Allowed(Path.Combine(_dir.Path, "disk2")));
    }

    [Fact]
    public void Admins_may_use_any_absolute_folder_unchanged()
    {
        var admin = new UserEntity { Id = Guid.NewGuid(), DisplayName = "root", Role = UserRole.Admin };

        Assert.True(_policy.TryResolve(admin, "/srv/media/tv", out var folder));
        Assert.Equal("/srv/media/tv", folder);
    }
}
