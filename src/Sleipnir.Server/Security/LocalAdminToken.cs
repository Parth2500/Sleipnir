using System.Security.Cryptography;
using System.Text;

namespace Sleipnir.Server.Security;

/// <summary>The secret that lets local clients (the TUI) act as admin on this machine.</summary>
public static class LocalAdminToken
{
    private const string FileName = "admin.token";

    public static string LoadOrCreate(string runtimeDir)
    {
        var path = Path.Combine(runtimeDir, FileName);
        for (var attempt = 0; ; attempt++)
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (IsWellFormed(existing))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        if (File.GetUnixFileMode(path) != ownerOnly) File.SetUnixFileMode(path, ownerOnly);
                    }
                    return existing;
                }

                // Empty (a crash between create and write) or not one of ours: the caller holds the
                // instance lock, so nobody else is using it. Replace it.
                File.Delete(path);
            }

            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            try
            {
                SecretFile.CreateNew(path, token);
                return token;
            }
            catch (IOException) when (attempt < 5)
            {
                // Another process created it first; read theirs on the next pass.
            }
        }
    }

    private static bool IsWellFormed(string token) =>
        token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
