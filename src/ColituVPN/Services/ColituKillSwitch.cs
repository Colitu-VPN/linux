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
/// Like the dynamic WFP session on Windows, the rules never outlive the app: a
/// small root watcher removes the table as soon as this process is gone, so a
/// crash cannot leave the computer offline.
/// </summary>
public sealed class ColituKillSwitch
{
    private const string Table = "colitu_killswitch";
    /// <summary>PID of the root watcher, in root-only /run, so a new engage replaces the old watcher.</summary>
    private const string WatcherPidFile = "/run/colitu-killswitch.watch";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Private, link-local and multicast ranges: printers, NAS, the router and DHCP keep working.</summary>
    internal static readonly string[] LocalNetworks4 = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "224.0.0.0/4", "255.255.255.255"];
    internal static readonly string[] LocalNetworks6 = ["fe80::/10", "fc00::/7", "ff00::/8"];
    internal static readonly string[] TunInterfaces = ["xray_tun", "singbox_tun"];

    public bool IsEngaged { get; private set; }

    /// <summary>Installs (or replaces) the rules. Throws when sudo refuses or nft fails.</summary>
    public async Task EngageAsync(IEnumerable<IPAddress> allowed)
    {
        var password = AppManager.Instance.LinuxSudoPwd;
        if (password.IsNullOrEmpty())
        {
            throw new InvalidOperationException("The kill switch needs the administrator password (TUN mode).");
        }
        var script = BuildEngageScript(allowed, Environment.ProcessId);
        var (code, error) = await RunAsRootAsync(script, password);
        if (code != 0)
        {
            throw new InvalidOperationException($"nft exited with {code}: {error.Trim()}");
        }
        IsEngaged = true;
    }

    public async Task ReleaseAsync()
    {
        if (!IsEngaged)
        {
            return;
        }
        IsEngaged = false;
        var password = AppManager.Instance.LinuxSudoPwd;
        if (password.IsNullOrEmpty())
        {
            return; // The watcher removes the table when the app exits.
        }
        try
        {
            await RunAsRootAsync($"nft delete table inet {Table} 2>/dev/null || true\n{StopWatcher}", password);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituKillSwitch.Release", ex);
        }
    }

    public void Release() => Task.Run(ReleaseAsync).Wait(CommandTimeout);

    internal static string BuildEngageScript(IEnumerable<IPAddress> allowed, int appPid)
    {
        var v4 = allowed.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
        var v6 = allowed.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).Select(a => a.ToString()).Distinct().ToList();

        var rules = new StringBuilder();
        rules.Append($"table inet {Table} {{").Append('\n');
        rules.Append("  chain output {").Append('\n');
        rules.Append("    type filter hook output priority 0; policy drop;").Append('\n');
        rules.Append("    oifname \"lo\" accept").Append('\n');
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

        // The previous watcher goes first (a stale one could delete the new table). Then the
        // table is replaced in ONE nft transaction ("add" makes the delete safe when there is
        // none), so there is no moment without rules. Finally watch this app: when it is gone,
        // or the table was removed by a release, the watcher deletes the table and exits.
        return $$"""
            set -e
            command -v nft >/dev/null 2>&1 || { echo "nft not found (install nftables)" >&2; exit 3; }
            {{StopWatcher}}
            nft -f - <<'COLITU_RULES'
            table inet {{Table}}
            delete table inet {{Table}}
            {{rules}}COLITU_RULES
            setsid sh -c 'while kill -0 {{appPid}} 2>/dev/null && nft list table inet {{Table}} >/dev/null 2>&1; do sleep 2; done; nft delete table inet {{Table}} 2>/dev/null; exit 0' >/dev/null 2>&1 < /dev/null &
            echo $! > {{WatcherPidFile}}
            exit 0
            """;
    }

    /// <summary>Stops the watcher of an earlier engage (root shell snippet).</summary>
    private static readonly string StopWatcher =
        $"if [ -r {WatcherPidFile} ]; then pid=$(cat {WatcherPidFile}); case \"$pid\" in ''|*[!0-9]*) ;; *) kill \"$pid\" 2>/dev/null || true ;; esac; rm -f {WatcherPidFile}; fi";

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
}
