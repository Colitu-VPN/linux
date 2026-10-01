using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Updates for the Linux packages. The release manifest
/// (colitu.com/downloads/linux/latest.json) is signed offline; the matching
/// .deb or .rpm is downloaded, checked against the signed SHA256 and installed
/// with pkexec, which asks the user through the desktop's own polkit prompt.
/// Installations that did not come from a package (a copied folder) open the
/// download page instead.
/// </summary>
public sealed class ColituUpdateService
{
    public static ColituUpdateService Instance { get; } = new();

    private static readonly int LocalVersionCode = ColituAuthService.VersionCode(ColituAuthService.ClientVersion);

    /// <summary>Folder under the data directory that holds a downloaded update (owner only).</summary>
    public const string UpdateFolderName = "guiUpdates";

    /// <summary>Directory the packages install to; anything else is a manual installation.</summary>
    public const string PackageInstallDir = "/opt/colitu-vpn";

    public const string DownloadPageUrl = $"{ColituAuthService.WebBaseUrl}/downloads/linux";

    private static string ManifestUrl =>
#if DEBUG
        Environment.GetEnvironmentVariable("COLITU_UPDATE_MANIFEST_URL")?.Trim().NullIfEmpty() ??
#endif
        $"{ColituAuthService.WebBaseUrl}/downloads/linux/latest.json";

    private static readonly string LocalVersionName = ColituAuthService.ClientVersion;
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    // HttpClient.Timeout also cancels content streaming, so packages get their own client with a generous limit.
    private readonly HttpClient _downloadClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<ColituUpdateInfo>? UpdateAvailable;

    private ColituUpdateService() { }

    /// <summary>The package format this installation came from, or null for a manual installation.</summary>
    public static string? InstalledPackageKind()
    {
        var exe = Utils.GetExePath();
        if (!exe.StartsWith(PackageInstallDir + "/", StringComparison.Ordinal))
        {
            return null;
        }
        if (File.Exists("/usr/bin/dpkg") && File.Exists("/var/lib/dpkg/info/colitu-vpn.list"))
        {
            return "deb";
        }
        if (File.Exists("/usr/bin/rpm"))
        {
            return "rpm";
        }
        return null;
    }

    public async Task<ColituUpdateInfo?> CheckForUpdateAsync(bool ignoreAttemptCache = false)
    {
        try
        {
            ClearCompletedAttempt();
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.TryAddWithoutValidation("X-Client-Platform", ColituAuthService.ClientPlatform);
            request.Headers.TryAddWithoutValidation("X-App-Version", LocalVersionName);
            request.Headers.TryAddWithoutValidation("User-Agent", $"ColituVPN/{LocalVersionName} Linux");
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<ColituLinuxVersionPayload>(_jsonOptions);
            if (payload == null) return null;
            if (!ColituUpdateSignature.Verify(payload))
            {
                Logging.SaveLog("ColituUpdateService: release manifest signature is missing or invalid; update ignored");
                return null;
            }

            var remoteCode = payload.LatestVersionCode;
            var remoteName = payload.VersionName ?? remoteCode.ToString(CultureInfo.InvariantCulture);
            if (!IsRemoteVersionNewer(remoteName, remoteCode)) return null;

            var kind = InstalledPackageKind();
            var package = kind == "deb" ? payload.Deb : kind == "rpm" ? payload.Rpm : null;
            if (!ignoreAttemptCache && WasRecentlyAttempted(remoteCode, package?.Url, package?.Sha256))
            {
                return null;
            }

            var info = new ColituUpdateInfo
            {
                VersionCode = remoteCode,
                CurrentVersion = LocalVersionName,
                NewVersion = remoteName,
                PackageKind = package == null ? null : kind,
                DownloadUrl = package?.Url ?? DownloadPageUrl,
                Sha256 = package?.Sha256,
                Force = payload.ForceUpdate,
                Notes = payload.ReleaseNotes ?? ""
            };
            if (info.CanInstall)
            {
                ValidateDownloadUrl(info.DownloadUrl);
            }
            UpdateAvailable?.Invoke(info);
            return info;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituUpdateService.CheckForUpdateAsync", ex);
            return null;
        }
    }

    public async Task DownloadUpdateAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadUpdateOnceAsync(info, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    private async Task DownloadUpdateOnceAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!info.CanInstall)
        {
            throw new InvalidOperationException("This installation cannot update itself.");
        }
        ValidateDownloadUrl(info.DownloadUrl);
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            throw new InvalidOperationException("The release manifest does not carry a SHA256 for the update.");
        }
        var updateDir = ColituHardening.CreateProtectedDirectory(Path.Combine(Utils.StartupPath(), UpdateFolderName));
        var destPath = Path.Combine(updateDir, $"colitu-vpn-{info.VersionCode}.{info.PackageKind}");

        using var response = await _downloadClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var dest = File.Create(destPath))
        {
            var buffer = new byte[65536];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                if (total > 0)
                {
                    progress?.Report(new ColituDownloadProgress
                    {
                        TotalBytes = total,
                        DownloadedBytes = downloaded,
                        Percent = (int)(downloaded * 100 / total)
                    });
                }
            }
        }

        VerifySha256(destPath, info.Sha256);
        info.LocalPath = destPath;
    }

    /// <summary>
    /// Installs the downloaded package through pkexec (polkit asks the user) and
    /// starts the new version once the package manager is done. The package's
    /// own scripts stop nothing: this process exits after the install succeeds.
    /// </summary>
    public async Task InstallAsync(ColituUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.LocalPath) || !File.Exists(info.LocalPath))
        {
            throw new InvalidOperationException("Update file not downloaded.");
        }
        // Checked again right before root sees the file.
        VerifySha256(info.LocalPath, info.Sha256!);
        SaveUpdateState(new ColituUpdateState
        {
            AttemptedVersionCode = info.VersionCode,
            AttemptedVersionName = info.NewVersion,
            DownloadUrl = info.DownloadUrl,
            Sha256 = info.Sha256,
            AttemptedAt = DateTimeOffset.Now
        });

        var arguments = info.PackageKind == "deb"
            ? new[] { "apt-get", "install", "-y", "--allow-downgrades", info.LocalPath }
            : File.Exists("/usr/bin/dnf")
                ? ["dnf", "install", "-y", info.LocalPath]
                : File.Exists("/usr/bin/zypper")
                    ? ["zypper", "--non-interactive", "install", "--allow-unsigned-rpm", info.LocalPath]
                    : ["rpm", "-U", "--force", info.LocalPath];

        var startInfo = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pkexec could not be started.");
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            // 126/127: the user dismissed the polkit prompt or pkexec is missing.
            throw new InvalidOperationException($"Package installation failed ({process.ExitCode}): {(await stderr).Trim()}");
        }
        try { File.Delete(info.LocalPath); } catch { }
    }

    /// <summary>Starts the freshly installed version after this process has exited.</summary>
    public static void RelaunchAfterExit()
    {
        var exe = Path.Combine(PackageInstallDir, "ColituVPN");
        var startInfo = new ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (var arg in new[] { "sh", "-c", $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.5; done; exec \"{exe}\"" })
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var _ = Process.Start(startInfo);
    }

    private static void VerifySha256(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            File.Delete(filePath);
            throw new InvalidOperationException("SHA256 verification failed. The downloaded file may be corrupted.");
        }
    }

    private static void ValidateDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Update download URL is invalid.");
        }
#if DEBUG
        if (uri.IsLoopback)
        {
            return;
        }
#endif
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Update download URL must use HTTPS.");
        }
        if (!TrustedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update download host is not trusted.");
        }
    }

    // Fixed list: the API address can be configured, so it must not widen where packages come from.
    private static readonly string[] TrustedDownloadHosts = ["colitu.com", "www.colitu.com", "api.colitu.com"];

    private static bool WasRecentlyAttempted(int versionCode, string? downloadUrl, string? sha256)
    {
        var state = LoadUpdateState();
        if (state?.AttemptedVersionCode != versionCode) return false;
        if (!string.Equals(state.DownloadUrl ?? "", downloadUrl ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(state.Sha256 ?? "", sha256 ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        return DateTimeOffset.Now - state.AttemptedAt < AttemptCooldown;
    }

    private static void ClearCompletedAttempt()
    {
        var state = LoadUpdateState();
        if (state == null) return;
        if (LocalVersionCode >= state.AttemptedVersionCode || DateTimeOffset.Now - state.AttemptedAt >= AttemptCooldown)
        {
            try { File.Delete(StatePath()); } catch { }
        }
    }

    private static ColituUpdateState? LoadUpdateState()
    {
        try
        {
            return File.Exists(StatePath())
                ? JsonSerializer.Deserialize<ColituUpdateState>(File.ReadAllText(StatePath()))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveUpdateState(ColituUpdateState state)
    {
        try
        {
            File.WriteAllText(StatePath(), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool IsRemoteVersionNewer(string? remoteVersionName, int remoteVersionCode)
    {
        var comparison = CompareSemanticVersions(remoteVersionName, LocalVersionName);
        return comparison.HasValue ? comparison.Value > 0 : remoteVersionCode > LocalVersionCode;
    }

    private static int? CompareSemanticVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        if (leftParts == null || rightParts == null) return null;

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    private static int[]? ParseVersionParts(string? version)
    {
        var clean = (version ?? "").Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)) clean = clean[1..];
        clean = clean.Split(['+', '-'], 2)[0];
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _))) return null;
        return parts.Select(int.Parse).ToArray();
    }

    private static string StatePath() => Utils.GetConfigPath("colitu-update-state.json");
}

public sealed class ColituUpdateInfo
{
    public int VersionCode { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    /// <summary>"deb" or "rpm" when this installation can update itself; null opens the download page.</summary>
    public string? PackageKind { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool Force { get; set; }
    public string Notes { get; set; } = "";
    public string? LocalPath { get; set; }
    public bool CanInstall => PackageKind != null && !string.IsNullOrWhiteSpace(Sha256);
}

public sealed class ColituDownloadProgress
{
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public int Percent { get; init; }
}

internal sealed class ColituLinuxVersionPayload
{
    public int LatestVersionCode { get; set; }
    public string? VersionName { get; set; }
    public bool ForceUpdate { get; set; }
    public string? ReleaseNotes { get; set; }
    public ColituLinuxPackage? Deb { get; set; }
    public ColituLinuxPackage? Rpm { get; set; }
    /// <summary>Base64 ECDSA P-256 signature over <see cref="ColituUpdateSignature.Message"/>.</summary>
    public string? Signature { get; set; }
}

internal sealed class ColituLinuxPackage
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>
/// The release manifest is signed offline with the same release key as the
/// Windows manifest; the "colitu-linux-update-v1" prefix keeps a Windows
/// signature from being replayed here. Packages are installed as root, so
/// HTTPS alone is not enough: a compromised website must not be able to hand
/// every client a package.
/// </summary>
internal static class ColituUpdateSignature
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEb7h8TtW4ekewQccnpdJo2i0fsJ28
        9gl8IkgEiNIAJvkcXryv7AZUf9O4qZboDzW7Jg2rYpnGJrVkxa1HDRmk8Q==
        -----END PUBLIC KEY-----
        """;

    /// <summary>The exact text scripts/sign-linux-manifest.py signs; changing any field breaks the signature.</summary>
    internal static string Message(ColituLinuxVersionPayload payload) => string.Join("\n",
        "colitu-linux-update-v1",
        payload.LatestVersionCode.ToString(CultureInfo.InvariantCulture),
        payload.VersionName ?? "",
        payload.Deb?.Url ?? "",
        (payload.Deb?.Sha256 ?? "").Trim().ToLowerInvariant(),
        payload.Rpm?.Url ?? "",
        (payload.Rpm?.Sha256 ?? "").Trim().ToLowerInvariant(),
        payload.ForceUpdate ? "true" : "false");

    internal static bool Verify(ColituLinuxVersionPayload payload, string publicKeyPem = PublicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature))
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(Message(payload)), Convert.FromBase64String(payload.Signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ColituUpdateState
{
    public int AttemptedVersionCode { get; set; }
    public string AttemptedVersionName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}
