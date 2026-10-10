using System.Diagnostics;
using Process = System.Diagnostics.Process;
namespace v2rayN.Desktop.Services;

/// <summary>
/// Opens links and folders for the user through the desktop's own handler
/// (xdg-open). Only web and mail links are accepted, never programs or files,
/// so nothing the panel or support sends can be executed through here.
/// </summary>
public static class ColituShell
{
    private static readonly string[] SystemDirectories = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>
    /// Absolute path of a system program (sudo, pkexec, apt-get, xdg-open...). Never looked
    /// up through PATH: a user-writable directory early in PATH (~/.local/bin) could otherwise
    /// put a fake sudo in front of the one that receives the administrator password.
    /// </summary>
    public static string SystemBinary(string name)
    {
        foreach (var directory in SystemDirectories)
        {
            var path = $"{directory}/{name}";
            if (File.Exists(path))
            {
                return path;
            }
        }
        return $"/usr/bin/{name}";
    }

    /// <summary>True for absolute https:// links (and mailto: when <paramref name="allowMail"/>).</summary>
    internal static bool IsSafeLink(string? url, bool allowMail = true)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (allowMail && uri.Scheme == Uri.UriSchemeMailto)
        {
            return true;
        }
        return uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !uri.IsUnc;
    }

    public static bool OpenUrl(string? url, bool allowMail = true)
    {
        if (!IsSafeLink(url, allowMail))
        {
            Logging.SaveLog($"ColituShell.OpenUrl: refused link with an unsupported scheme or form ({Truncate(url)})");
            return false;
        }
        // Commas in a server-provided link are percent-encoded (same meaning to a web server) so
        // no handler that splits its command line at commas can read part of it as a switch.
        return XdgOpen(new Uri(url!.Trim()).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal));
    }

    /// <summary>Shows one of the app's own folders (logs, downloaded attachments).</summary>
    public static bool OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var allowed = new[] { Utils.StartupPath(), ColituSupportService.DownloadFolder }
            .Select(Path.GetFullPath)
            .Any(root => full == root || full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal));
        if (!allowed || !Directory.Exists(full))
        {
            return false;
        }
        return XdgOpen(full);
    }

    private static bool XdgOpen(string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo(SystemBinary("xdg-open")) { UseShellExecute = false };
            startInfo.ArgumentList.Add(target);
            using var _ = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituShell.XdgOpen", ex);
            return false;
        }
    }

    private static string Truncate(string? value) => value == null ? "" : value.Length <= 80 ? value : value[..80];
}
