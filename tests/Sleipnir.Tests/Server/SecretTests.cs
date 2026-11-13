using Sleipnir.Server.Security;

namespace Sleipnir.Tests.Server;

public class SecretTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleipnir-secret-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateNew_makes_the_file_private_and_its_folder_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "run", "secret");

        SecretFile.CreateNew(path, "hunter2");

        Assert.Equal("hunter2", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void CreateNew_refuses_to_overwrite_and_keeps_the_old_content()
    {
        var path = Path.Combine(_dir, "secret");
        SecretFile.CreateNew(path, "first");

        Assert.Throws<IOException>(() => SecretFile.CreateNew(path, "second"));

        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void LoadOrCreate_returns_the_same_43_character_token_each_time()
    {
        var first = LocalAdminToken.LoadOrCreate(_dir);
        var second = LocalAdminToken.LoadOrCreate(_dir);

        Assert.Equal(43, first.Length);
        Assert.Equal(first, second);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first);
    }

    [Fact]
    public void Tokens_from_different_folders_differ()
    {
        Assert.NotEqual(LocalAdminToken.LoadOrCreate(Path.Combine(_dir, "a")), LocalAdminToken.LoadOrCreate(Path.Combine(_dir, "b")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abd1234567890123456789012345678901234567890")]
    public void Matches_rejects_missing_short_and_wrong_tokens(string? presented)
    {
        Assert.False(LocalAdminToken.Matches("abc1234567890123456789012345678901234567890", presented));
    }

    [Fact]
    public void Matches_accepts_the_exact_token()
    {
        Assert.True(LocalAdminToken.Matches("abc", "abc"));
    }
}
