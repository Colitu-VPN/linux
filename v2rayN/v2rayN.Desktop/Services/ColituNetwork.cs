using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Network facts the VPN needs before it starts the core: the server's real
/// address, the physical interface to send through, and whether another VPN is
/// already holding the default route. Routes come from /proc/net/route.
/// </summary>
public static class ColituNetwork
{
    private static readonly HttpClient Doh = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4) }) { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Dictionary<string, (IPAddress Address, DateTimeOffset Until)> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    /// <summary>DNS-over-HTTPS resolvers used ahead of the core; the kill switch lets them through.</summary>
    internal static readonly string[] DohEndpoints = ["https://cloudflare-dns.com/dns-query", "https://dns.google/resolve"];

    /// <summary>
    /// Resolves a server host name to a real address, asking DNS-over-HTTPS
    /// first. Other VPN clients running in "fake IP" mode (Clash, sing-box)
    /// answer every system DNS query with a private placeholder address; the
    /// core would then dial that placeholder on the physical interface and hang.
    /// Returns null when only such placeholders are available.
    /// </summary>
    public static async Task<IPAddress?> ResolveServerAsync(string host, CancellationToken token = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        lock (CacheGate)
        {
            if (ResolveCache.TryGetValue(host, out var cached) && cached.Until > DateTimeOffset.UtcNow)
            {
                return cached.Address;
            }
        }

        var resolved = await ResolveViaDohAsync(host, token) ?? await ResolveViaSystemAsync(host, token);
        if (resolved != null)
        {
            lock (CacheGate)
            {
                ResolveCache[host] = (resolved, DateTimeOffset.UtcNow.AddMinutes(10));
            }
        }
        return resolved;
    }

    /// <summary>Every address resolved so far (VPN servers, the panel); the kill switch keeps them reachable.</summary>
    internal static IReadOnlyList<IPAddress> KnownAddresses()
    {
        lock (CacheGate)
        {
            return ResolveCache.Values.Select(item => item.Address).Distinct().ToList();
        }
    }

    private static async Task<IPAddress?> ResolveViaDohAsync(string host, CancellationToken token)
    {
        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");
                request.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var response = await Doh.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!document.RootElement.TryGetProperty("Answer", out var answers))
                {
                    continue;
                }
                foreach (var answer in answers.EnumerateArray())
                {
                    if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                        && answer.TryGetProperty("data", out var data)
                        && IPAddress.TryParse(data.GetString(), out var address)
                        && !IsPlaceholder(address))
                    {
                        return address;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Logging.SaveLog($"ColituNetwork.ResolveViaDoh {endpoint}: {ex.Message}");
            }
        }
        return null;
    }

    private static async Task<IPAddress?> ResolveViaSystemAsync(string host, CancellationToken token)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
            return addresses.FirstOrDefault(address => !IsPlaceholder(address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituNetwork.ResolveViaSystem {host}: {ex.Message}");
            return null;
        }
    }

    /// <summary>198.18.0.0/15 is the pool fake-IP DNS resolvers hand out; it is never a real server.</summary>
    internal static bool IsPlaceholder(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    /// <summary>
    /// The physical interface (Ethernet or Wi-Fi) that carries the internet
    /// connection: up, with an IPv4 default route, lowest metric. Virtual
    /// interfaces of other VPN clients, containers and VMs are skipped so the
    /// core never sends its traffic into another tunnel.
    /// </summary>
    public static string? PhysicalInterfaceName()
    {
        try
        {
            var routes = DefaultRoutes();
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
                .Select(adapter => adapter.Name)
                .ToHashSet(StringComparer.Ordinal);
            return routes
                .Where(route => up.Contains(route.Interface) && !IsVirtual(route.Interface))
                .OrderBy(route => route.Metric)
                .ThenBy(route => IsWireless(route.Interface) ? 1 : 0)
                .Select(route => route.Interface)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNetwork.PhysicalInterfaceName", ex);
            return null;
        }
    }

    /// <summary>True once any interface has an IPv4 default route (the network is usable).</summary>
    public static bool HasDefaultRoute()
    {
        try
        {
            return DefaultRoutes().Count > 0;
        }
        catch
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
    }

    /// <summary>Name of another VPN client's interface that currently owns a default route, if any.</summary>
    public static string? CompetingVpnAdapter()
    {
        try
        {
            return DefaultRoutes()
                .Select(route => route.Interface)
                .Where(IsVirtual)
                // Colitu's own TUN interfaces (Xray and sing-box) are not "another VPN".
                .FirstOrDefault(name => !IsOwnTun(name));
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsOwnTun(string name) =>
        name.Contains("xray_tun", StringComparison.Ordinal) || name.Contains("singbox_tun", StringComparison.Ordinal);

    private static readonly string[] VirtualPrefixes =
    [
        "lo", "tun", "tap", "wg", "ppp", "utun", "docker", "br-", "veth", "virbr", "vmnet", "vboxnet",
        "zt", "tailscale", "nordlynx", "proton", "mullvad", "cni", "flannel", "kube", "lxc", "lxd", "podman", "ipsec", "vti", "gre", "sit"
    ];

    internal static bool IsVirtual(string name)
    {
        if (VirtualPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) || IsOwnTun(name))
        {
            return true;
        }
        // Physical devices have a "device" link in sysfs; virtual ones do not.
        try
        {
            return Directory.Exists("/sys/class/net") && !Directory.Exists($"/sys/class/net/{name}/device");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWireless(string name)
    {
        try
        {
            return Directory.Exists($"/sys/class/net/{name}/wireless");
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct DefaultRoute(string Interface, int Metric);

    /// <summary>IPv4 default routes from /proc/net/route (destination and mask 0).</summary>
    private static List<DefaultRoute> DefaultRoutes()
    {
        var result = new List<DefaultRoute>();
        const string table = "/proc/net/route";
        if (!File.Exists(table))
        {
            return result;
        }
        foreach (var line in File.ReadLines(table).Skip(1))
        {
            // Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT
            var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 8 || cols[1] != "00000000" || cols[7] != "00000000")
            {
                continue;
            }
            var flags = Convert.ToInt32(cols[3], 16);
            if ((flags & 0x1) == 0)
            {
                continue; // RTF_UP
            }
            result.Add(new DefaultRoute(cols[0], int.TryParse(cols[6], out var metric) ? metric : int.MaxValue));
        }
        return result;
    }
}
