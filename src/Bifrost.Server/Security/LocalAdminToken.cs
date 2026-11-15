using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Server.Security;

/// <summary>The secret that lets the host admin act on this machine until step 2 brings the admin socket (design §5).</summary>
public static class LocalAdminToken
{
    private const string FileName = "admin.token";
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyFolder = OwnerOnlyFile | UnixFileMode.UserExecute;

    public static string LoadOrCreate(string runtimeDir)
    {
        LockDownFolder(runtimeDir);
        var path = Path.Combine(runtimeDir, FileName);
        for (var attempt = 0; ; attempt++)
        {
            var info = new FileInfo(path);
            if (info.Exists || info.LinkTarget is not null)
            {
                if (IsTrustworthy(info) && File.ReadAllText(path).Trim() is var existing && IsWellFormed(existing))
                {
                    return existing;
                }

                // Empty (a crash between create and write), not one of ours, a link, or readable by
                // others (someone may have copied it): never reuse it. File.Delete removes a link, not
                // its target. The caller holds the instance lock, so nobody else is using it.
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

    /// <summary>A plain file, not a link, that only its owner could ever have read.</summary>
    private static bool IsTrustworthy(FileInfo info) =>
        info.LinkTarget is null
        && (info.Attributes & FileAttributes.ReparsePoint) == 0
        && (OperatingSystem.IsWindows() || info.UnixFileMode == OwnerOnlyFile);

    /// <summary>A folder others can list or enter would let them swap the file; systemd creates it 0755 by default.</summary>
    private static void LockDownFolder(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(dir, OwnerOnlyFolder);
        if (File.GetUnixFileMode(dir) != OwnerOnlyFolder) File.SetUnixFileMode(dir, OwnerOnlyFolder);
    }

    private static bool IsWellFormed(string token) =>
        token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool Matches(string expected, string? presented)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
