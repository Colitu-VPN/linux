using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Local data protection on Linux. The folders that hold sessions, generated
/// core configs (server credentials) and logs are readable by their owner only
/// (0700), and tokens are additionally encrypted with a key bound to this
/// machine and user, so a copied config folder is useless elsewhere.
/// </summary>
public static class ColituHardening
{
    /// <summary>Folders that may contain credentials, tokens or connection details.</summary>
    private static readonly string[] DataFolders = ["guiConfigs", "binConfigs", "guiLogs", "guiTemps", "guiBackups", ColituUpdateService.UpdateFolderName];

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    public static void HardenDataFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        TryRestrict(Utils.StartupPath());
        foreach (var name in DataFolders)
        {
            var path = Path.Combine(Utils.StartupPath(), name);
            try
            {
                Directory.CreateDirectory(path);
                TryRestrict(path);
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituHardening.HardenDataFolders {name}", ex);
            }
        }
    }

    private static void TryRestrict(string directory)
    {
        try
        {
            // Only the user's own data folders (~/.local/share/ColituVPN, or a portable copy in
            // the home directory). A packaged install under /opt or /usr/lib stays as packaged:
            // HasWritePermission() looks at the install folder, so it was false for every
            // packaged install and the data folders kept the umask (0755).
            if (!OperatingSystem.IsWindows() && Directory.Exists(directory) && IsInHome(directory))
            {
                File.SetUnixFileMode(directory, OwnerOnlyDirectory);
            }
        }
        catch
        {
            // Read-only installation folder: nothing to restrict there.
        }
    }

    private static bool IsInHome(string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || home == "/")
        {
            return false;
        }
        var full = Path.GetFullPath(directory).TrimEnd('/');
        home = Path.GetFullPath(home).TrimEnd('/');
        return full.StartsWith(home + "/", StringComparison.Ordinal);
    }

    /// <summary>Creates an empty folder that only the current user can read or change.</summary>
    public static string CreateProtectedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
        return path;
    }

    /// <summary>Writes a file atomically and readable by the owner only.</summary>
    public static void WritePrivateFile(string path, byte[] content)
    {
        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temp, content);
        }
        else
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = OwnerOnlyFile
            });
            stream.Write(content);
        }
        File.Move(temp, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerOnlyFile);
        }
    }

    public static void WritePrivateText(string path, string content) => WritePrivateFile(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Logs older than a week are deleted before logging starts.</summary>
    public static void CleanLogs()
    {
        try
        {
            var folder = Path.Combine(Utils.StartupPath(), "guiLogs");
            if (!Directory.Exists(folder))
            {
                return;
            }
            var cutoff = DateTime.UtcNow - LogRetention;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // Logging is not set up yet; a failed cleanup must not stop the app.
        }
    }

    /// <summary>
    /// Per-user file in guiConfigs. A portable copy in a shared folder may be
    /// started by several users; each keeps its own session.
    /// </summary>
    public static string UserConfigPath(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Utils.GetConfigPath($"{stem}-{UserKey.Value}{extension}");
    }

    private static readonly Lazy<string> UserKey = new(() =>
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("colitu-user-v1|" + UserSeed()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    });

    // ── Secrets (replaces Windows DPAPI) ───────────────────────────────────
    private const byte SecretVersion = 1;

    /// <summary>AES-GCM with a key derived from the machine id, the user and a random per-user salt.</summary>
    public static string Protect(string value) => Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(value)));

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(value)));
    }

    public static byte[] ProtectBytes(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(SecretKey.Value, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        return [SecretVersion, .. nonce, .. tag, .. cipher];
    }

    public static byte[] UnprotectBytes(byte[] data)
    {
        if (data.Length < 29 || data[0] != SecretVersion)
        {
            throw new CryptographicException("Unknown secret format.");
        }
        var nonce = data.AsSpan(1, 12);
        var tag = data.AsSpan(13, 16);
        var cipher = data.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(SecretKey.Value, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static readonly Lazy<byte[]> SecretKey = new(() =>
    {
        var saltPath = UserConfigPath("colitu-key.bin");
        byte[] salt;
        try
        {
            salt = File.Exists(saltPath) ? File.ReadAllBytes(saltPath) : [];
        }
        catch
        {
            salt = [];
        }
        if (salt.Length != 32)
        {
            salt = RandomNumberGenerator.GetBytes(32);
            WritePrivateFile(saltPath, salt);
        }
        var seed = Encoding.UTF8.GetBytes($"colitu-linux-secret-v1|{MachineId()}|{UserSeed()}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, salt);
    });

    internal static string MachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                var id = File.ReadAllText(path).Trim();
                if (id.Length > 0)
                {
                    return id;
                }
            }
            catch
            {
                // Try the next location.
            }
        }
        return Environment.MachineName;
    }

    private static string UserSeed()
    {
        try
        {
            return OperatingSystem.IsWindows() ? Environment.UserName : $"uid:{getuid()}";
        }
        catch
        {
            return Environment.UserName;
        }
    }

    [DllImport("libc", SetLastError = false)]
    private static extern uint getuid();
}
