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
                if (existing.Length > 0) return existing;
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

    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
