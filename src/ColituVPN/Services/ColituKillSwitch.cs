using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net;
using System.Net.Sockets;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Linux kill switch on nftables. While engaged, an output chain with a drop
/// policy lets through only loopback, Colitu's TUN interfaces, the local
/// network and DHCP, the VPN servers and the Colitu API (so the tunnel can come
/// back), and nothing else. The rules are installed with the sudo password the
/// TUN mode already asked for.
///
/// The switch fails closed: the rules stay in place when the app dies (crash,
/// kill -9, OOM). Only a deliberate release removes them: the user turning
/// protection off or quitting, a sign-out, the session ending, the
/// "--colitu-cleanup" command line, or removing the package. A root-written
/// marker in /run records that the rules are in place, so the next start knows
/// (without root) that it must re-adopt or remove them. /run and nftables rules
/// both disappear on reboot, so the marker never outlives the rules there.
/// </summary>
public sealed class ColituKillSwitch
{
    public const string Table = "colitu_killswitch";
    /// <summary>"&lt;uid&gt; &lt;pid&gt;" of the app that installed the rules; written by root, readable by everyone.</summary>
    public const string MarkerFile = "/run/colitu-killswitch.active";
    /// <summary>PID file of the auto-delete watcher used up to 1.1.2; stopped so an old one cannot remove new rules.</summary>
    private const string LegacyWatcherPidFile = "/run/colitu-killswitch.watch";
    private const string ProcessName = "ColituVPN";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Private, link-local and multicast ranges: printers, NAS, the router and DHCP keep working.</summary>
    internal static readonly string[] LocalNetworks4 = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "224.0.0.0/4", "255.255.255.255"];
    internal static readonly string[] LocalNetworks6 = ["fe80::/10", "fc00::/7", "ff00::/8"];
    internal static readonly string[] TunInterfaces = ["xray_tun", "singbox_tun"];

    public bool IsEngaged { get; private set; }

    /// <summary>
    /// Takes over rules a previous run left in place (it crashed or was killed while
    /// they were engaged). They keep blocking until a reconnect replaces them or a
    /// release removes them.
    /// </summary>
    public void Adopt() => IsEngaged = true;

    /// <summary>
    /// Installs (or replaces) the rules. <paramref name="bypassNetworks"/> are split-tunneling
    /// addresses the user sends around the VPN; they stay reachable while it is down too.
    /// Throws when sudo refuses or nft fails.
    /// </summary>
    public async Task EngageAsync(IEnumerable<IPAddress> allowed, IEnumerable<string>? bypassNetworks = null)
    {
        var password = AppManager.Instance.LinuxSudoPwd;
        if (password.IsNullOrEmpty())
        {
            throw new InvalidOperationException("The kill switch needs the administrator password (TUN mode).");
        }
        var script = BuildEngageScript(allowed, Environment.ProcessId, bypassNetworks);
        var (code, error) = await RunAsRootAsync(script, password);
        if (code != 0)
        {
            throw new InvalidOperationException($"nft exited with {code}: {error.Trim()}");
        }
        IsEngaged = true;
    }

    /// <summary>
    /// Removes the rules. False when they are still in place: without the sudo password
    /// (rules adopted from a crashed run) nothing can be removed, and a failed nft keeps
    /// the switch engaged rather than pretending the internet is open.
    /// </summary>
    public async Task<bool> ReleaseAsync()
    {
        if (!IsEngaged)
        {
            return true;
        }
        var password = AppManager.Instance.LinuxSudoPwd;
        if (password.IsNullOrEmpty())
        {
            return false;
        }
        try
        {
            var (code, error) = await RunAsRootAsync(ReleaseScript, password);
            if (code != 0)
            {
                Logging.SaveLog($"ColituKillSwitch.Release: exit {code}: {error.Trim()}");
                return false;
            }
            IsEngaged = false;
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituKillSwitch.Release", ex);
            return false;
        }
    }

    public bool Release()
    {
        try
        {
            var task = Task.Run(ReleaseAsync);
            return task.Wait(CommandTimeout) && task.Result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituKillSwitch.Release", ex);
            return false;
        }
    }

    internal static string BuildEngageScript(IEnumerable<IPAddress> allowed, int appPid, IEnumerable<string>? bypassNetworks = null)
    {
        var v4 = allowed.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
        var v6 = allowed.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).Select(a => a.ToString()).ToList();
        // Only well-formed networks reach root's nft (the list is user input); the parsed form is written.
        foreach (var network in bypassNetworks ?? [])
        {
            if (ColituSplitTunnel.TryNormalizeNetwork(network, out var normalized, out var family))
            {
                (family == AddressFamily.InterNetwork ? v4 : v6).Add(normalized);
            }
        }
        v4 = v4.Distinct().ToList();
        v6 = v6.Distinct().ToList();

        var rules = new StringBuilder();
        rules.Append($"table inet {Table} {{").Append('\n');
        rules.Append("  chain output {").Append('\n');
        rules.Append("    type filter hook output priority 0; policy drop;").Append('\n');
        rules.Append("    oifname \"lo\" accept").Append('\n');
        // Connections the TUN core deliberately sends around the tunnel (split tunneling,
        // Russian sites, the cores' own traffic) carry this mark; only root can set it.
        rules.Append($"    meta mark 0x{Global.LinuxTunDirectRoutingMark:x} accept").Append('\n');
        rules.Append($"    oifname {{ {string.Join(", ", TunInterfaces.Select(name => $"\"{name}\""))} }} accept").Append('\n');
        rules.Append($"    ip daddr {{ {string.Join(", ", LocalNetworks4)} }} accept").Append('\n');
        rules.Append($"    ip6 daddr {{ {string.Join(", ", LocalNetworks6)} }} accept").Append('\n');
        rules.Append("    udp sport 68 udp dport 67 accept").Append('\n');
        rules.Append("    udp dport 547 accept").Append('\n');
        if (v4.Count > 0)
        {
            rules.Append($"    ip daddr {{ {string.Join(", ", v4)} }} accept").Append('\n');
        }
        if (v6.Count > 0)
        {
            rules.Append($"    ip6 daddr {{ {string.Join(", ", v6)} }} accept").Append('\n');
        }
        rules.Append("  }").Append('\n');
        rules.Append("}").Append('\n');

        // An auto-delete watcher of an older version goes first (it would remove the new
        // table). Then the table is replaced in ONE nft transaction ("add" makes the delete
        // safe when there is none), so there is no moment without rules. Nothing watches
        // this app: the rules stay until a deliberate release. The marker tells the next
        // start (and the user's own account, without root) that they are in place.
        return $$"""
            set -e
            command -v nft >/dev/null 2>&1 || { echo "nft not found (install nftables)" >&2; exit 3; }
            {{StopLegacyWatcher}}
            nft -f - <<'COLITU_RULES'
            table inet {{Table}}
            delete table inet {{Table}}
            {{rules}}COLITU_RULES
            umask 022
            printf '%s %s\n' "${SUDO_UID:-0}" {{appPid}} > {{MarkerFile}}
            chmod 0644 {{MarkerFile}}
            exit 0
            """;
    }

    /// <summary>Root shell script that removes the rules, the marker and an old watcher. Safe when none exist.</summary>
    internal static readonly string ReleaseScript =
        $"nft delete table inet {Table} 2>/dev/null || true\nrm -f {MarkerFile}\n{StopLegacyWatcher}\nexit 0\n";

    /// <summary>Stops the watcher of a version up to 1.1.2 (root shell snippet).</summary>
    private static string StopLegacyWatcher =>
        $"if [ -r {LegacyWatcherPidFile} ]; then pid=$(cat {LegacyWatcherPidFile}); case \"$pid\" in ''|*[!0-9]*) ;; *) kill \"$pid\" 2>/dev/null || true ;; esac; rm -f {LegacyWatcherPidFile}; fi";

    // ── Rules left by a previous run ───────────────────────────────────────
    /// <summary>
    /// True when the marker says the rules are in place and no running Colitu owns them,
    /// i.e. a previous run died while they were engaged. Rules of a Colitu that still runs
    /// (another user's session) are left alone.
    /// </summary>
    public static bool HasLeftoverRules()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        try
        {
            if (!File.Exists(MarkerFile))
            {
                return false;
            }
            var owner = ParseMarkerPid(File.ReadAllText(MarkerFile));
            return owner is not int pid || pid == Environment.ProcessId || !IsColituProcess(pid);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituKillSwitch.HasLeftoverRules", ex);
            return false;
        }
    }

    /// <summary>The app PID from the marker ("uid pid"); null when unreadable.</summary>
    internal static int? ParseMarkerPid(string? text)
    {
        var parts = (text ?? "").Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    private static bool IsColituProcess(int pid)
    {
        try
        {
            var comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            return string.Equals(comm, ProcessName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Root shell script that stops VPN cores of a crashed run that still run as root
    /// (TUN mode starts them through sudo; they outlive the app and keep the TUN routes).
    /// Matches on the executable path, which must be under one of <paramref name="binPaths"/>.
    /// </summary>
    internal static string BuildStopOrphanCoresScript(params string[] binPaths)
    {
        var cases = string.Join("\n", binPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.TrimEnd('/') + "/")
            .Distinct(StringComparer.Ordinal)
            .Select(prefix => $"    {ShellQuote(prefix)}*) kill -9 \"${{p#/proc/}}\" 2>/dev/null || true ;;"));
        return $"""
            for p in /proc/[0-9]*; do
              exe=$(readlink "$p/exe" 2>/dev/null) || continue
              case "$exe" in
            {cases}
              esac
            done
            exit 0
            """;
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // ── Command line: colitu-vpn --colitu-cleanup ──────────────────────────
    public const string CleanupArg = "--colitu-cleanup";

    /// <summary>
    /// Removes the kill switch rules (and an old watcher) from a terminal or a script, for
    /// when the app cannot start or was removed without its package scripts. As root it
    /// runs nft directly; otherwise through sudo in a terminal, or pkexec without one.
    /// Returns the process exit code.
    /// </summary>
    public static int RunCommandLineCleanup()
    {
        try
        {
            ProcessStartInfo startInfo;
            if (Environment.UserName == "root")
            {
                startInfo = new ProcessStartInfo(Global.LinuxBash);
            }
            else if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Colitu VPN: removing the kill switch rules needs your administrator (sudo) password.");
                startInfo = new ProcessStartInfo(ColituShell.SystemBinary("sudo"));
                startInfo.ArgumentList.Add("--");
                startInfo.ArgumentList.Add(Global.LinuxBash);
            }
            else
            {
                startInfo = new ProcessStartInfo(ColituShell.SystemBinary("pkexec"));
                startInfo.ArgumentList.Add(Global.LinuxBash);
            }
            startInfo.UseShellExecute = false;
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(ReleaseScript);
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Colitu VPN: could not start the cleanup.");
                return 1;
            }
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                Console.WriteLine("Colitu VPN: kill switch rules removed. The internet is no longer blocked by Colitu.");
            }
            else
            {
                Console.Error.WriteLine($"Colitu VPN: cleanup failed (exit {process.ExitCode}). Run: sudo nft delete table inet {Table}");
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Colitu VPN: cleanup failed: {ex.Message}. Run: sudo nft delete table inet {Table}");
            return 1;
        }
    }

    /// <summary>Runs a bash script through sudo with the password on stdin.</summary>
    internal static async Task<(int ExitCode, string Error)> RunAsRootAsync(string script, string password)
    {
        var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("sudo"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-S", "-p", "", "--", Global.LinuxBash, "-c", script })
        {
            startInfo.ArgumentList.Add(arg);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("sudo could not be started.");
        await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(CommandTimeout);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("sudo did not finish in time.");
        }
        return (process.ExitCode, await error);
    }

    /// <summary>Checks a sudo password without running anything (sudo -v).</summary>
    public static async Task<bool> VerifySudoPasswordAsync(string password)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("sudo"))
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "-S", "-k", "-p", "", "-v" })
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }
            await process.StandardInput.WriteLineAsync(password);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituKillSwitch.VerifySudoPassword", ex);
            return false;
        }
    }

    /// <summary>True when sudo is installed, i.e. TUN mode (and the kill switch) can work here.</summary>
    public static bool SudoAvailable => OperatingSystem.IsLinux() && File.Exists(ColituShell.SystemBinary("sudo"));
}
