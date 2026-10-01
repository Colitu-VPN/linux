using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Net;
using System.Net.Sockets;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Ping of a location: the TCP connect time from this computer to the node's
/// latency probe address (the panel's latency_host / latency_port), best of two
/// tries, like the phone apps. The socket is bound to the physical interface
/// with SO_BINDTODEVICE where the kernel allows it, so the number describes this
/// computer's own path to the server even while the VPN is connected.
/// </summary>
public static class ColituLatency
{
    private const int TimeoutMs = 2500;
    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;

    /// <summary>Pings of every server with a probe address, by server id.</summary>
    public static async Task<Dictionary<string, int>> MeasureAllAsync(IEnumerable<ColituVpnServer> servers)
    {
        var device = ColituNetwork.PhysicalInterfaceName();
        var probes = servers
            .Where(server => server.Id.IsNotEmpty() && server.Host.IsNotEmpty() && server.Port is > 0 and < 65536)
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(async server => (server.Id!, await MeasureAsync(server.Host!, server.Port!.Value, device)));
        var results = await Task.WhenAll(probes);
        return results
            .Where(result => result.Item2 != null)
            .ToDictionary(result => result.Item1, result => result.Item2!.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<int?> MeasureAsync(string host, int port, string? device)
    {
        IPAddress? address;
        try
        {
            // Resolved first (DNS over HTTPS, cached) so the lookup is not counted as ping
            // and a fake-IP answer from another VPN client is never dialled.
            using var resolveTimeout = new CancellationTokenSource(TimeoutMs);
            address = await ColituNetwork.ResolveServerAsync(host, resolveTimeout.Token);
        }
        catch
        {
            return null;
        }
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        int? best = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ms = await ConnectMsAsync(address, port, device);
            if (ms != null && (best == null || ms < best))
            {
                best = ms;
            }
        }
        return best;
    }

    private static async Task<int?> ConnectMsAsync(IPAddress address, int port, string? device)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (device.IsNotEmpty() && OperatingSystem.IsLinux())
            {
                try
                {
                    socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
                }
                catch (SocketException)
                {
                    // Kernels before 5.7 need CAP_NET_RAW: the measurement still works, possibly through the tunnel.
                }
            }
            using var timeout = new CancellationTokenSource(TimeoutMs);
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            watch.Stop();
            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }
}
