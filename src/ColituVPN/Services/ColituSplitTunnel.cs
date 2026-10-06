using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

public static class ColituSplitTunnelModes
{
    public const string Off = "off";
    /// <summary>The selected apps and sites bypass the VPN; everything else uses it.</summary>
    public const string Exclude = "exclude";
    /// <summary>Only the selected apps and sites use the VPN; everything else goes direct.</summary>
    public const string Include = "include";

    public static string Normalize(string? mode)
    {
        var value = (mode ?? "").Trim().ToLowerInvariant();
        return value is Exclude or Include ? value : Off;
    }
}

/// <summary>
/// Split tunneling: validates the user's lists and turns them into routing rules for the
/// cores. On Linux the TUN adapter always belongs to sing-box (alone for Hysteria2, in
/// front of Xray for the other transports), so app rules (process name or path) work in
/// TUN mode only and are pinned to sing-box's "tun" inbound. Domains and IPs work in both
/// modes (sing-box and Xray routing; in proxy mode also the desktop's proxy bypass list).
/// </summary>
public static class ColituSplitTunnel
{
    /// <summary>Tag of sing-box's TUN inbound (ServiceLib Sample/tun_singbox_inbound).</summary>
    public const string TunInboundTag = "tun";
    public const int MaxEntries = 200;

    private static readonly Regex DomainLabel = new("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Text = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProcessName = new(@"^[A-Za-z0-9._+\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Validation ─────────────────────────────────────────────────────────
    /// <summary>
    /// "https://Mail.Example.com/inbox", "*.example.com" or "example.com." become
    /// "mail.example.com" / "example.com" (internationalized names as punycode). Matches
    /// the name and every subdomain.
    /// </summary>
    public static bool TryNormalizeDomain(string? input, out string domain)
    {
        domain = "";
        var value = (input ?? "").Trim().Trim('"', '\'').ToLowerInvariant();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }
        var end = value.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
        {
            value = value[..end];
        }
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            value = value[(at + 1)..];
        }
        var colon = value.LastIndexOf(':');
        if (colon >= 0 && value.IndexOf(':') == colon && value[(colon + 1)..].All(char.IsAsciiDigit))
        {
            value = value[..colon];
        }
        while (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        value = value.TrimStart('.').TrimEnd('.');
        if (value.Length == 0 || IPAddress.TryParse(value, out _))
        {
            return false;
        }
        try
        {
            value = new IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
        var labels = value.Split('.');
        if (value.Length > 253 || labels.Length < 2 || !labels.All(label => DomainLabel.IsMatch(label)) || labels[^1].All(char.IsAsciiDigit))
        {
            return false;
        }
        domain = value;
        return true;
    }

    /// <summary>
    /// "203.0.113.7", "10.0.0.0/8", "2001:db8::/32". Host bits are cleared ("10.1.2.3/8" is
    /// 10.0.0.0/8); a single address keeps its plain form. The whole internet (/0) is refused.
    /// </summary>
    public static bool TryNormalizeNetwork(string? input, out string network, out AddressFamily family)
    {
        network = "";
        family = AddressFamily.Unspecified;
        var value = (input ?? "").Trim();
        var parts = value.Split('/');
        if (parts.Length > 2 || parts[0].Contains('%'))
        {
            return false;
        }
        var text = parts[0];
        var looksV4 = Ipv4Text.IsMatch(text);
        if (!looksV4 && !text.Contains(':'))
        {
            return false;
        }
        if (!IPAddress.TryParse(text, out var address))
        {
            return false;
        }
        family = address.AddressFamily;
        var bits = family == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 1 || prefix > bits))
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bits; bit++)
        {
            bytes[bit / 8] &= unchecked((byte)~(0x80 >> (bit % 8)));
        }
        var baseAddress = new IPAddress(bytes);
        network = prefix == bits ? baseAddress.ToString() : $"{baseAddress}/{prefix}";
        return true;
    }

    public static bool TryNormalizeNetwork(string? input, out string network) => TryNormalizeNetwork(input, out network, out _);

    /// <summary>A program name ("firefox", "telegram-desktop") or its absolute path ("/usr/lib/firefox/firefox").</summary>
    public static bool TryNormalizeApp(string? input, out string app)
    {
        app = "";
        var value = (input ?? "").Trim().Trim('"', '\'');
        if (value.StartsWith('/'))
        {
            if (value.Length > 1024 || value.EndsWith('/') || value.Contains("/../") || value.EndsWith("/..")
                || value.Any(c => char.IsControl(c) || c is ',' or '"' or '\\'))
            {
                return false;
            }
            app = value;
            return true;
        }
        if (!ProcessName.IsMatch(value) || value is "." or "..")
        {
            return false;
        }
        app = value;
        return true;
    }

    /// <summary>Splits pasted text into entries; separators are new lines, commas and semicolons (and spaces, except for app paths).</summary>
    public static List<string> SplitEntries(string? text, bool allowSpaces = false)
    {
        var separators = allowSpaces ? new[] { '\n', '\r', ',', ';' } : new[] { '\n', '\r', ',', ';', ' ', '\t' };
        return (text ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public delegate bool Normalizer(string? input, out string value);

    /// <summary>Valid entries (normalized, without duplicates, at most <see cref="MaxEntries"/>) and the rejected ones.</summary>
    public static (List<string> Valid, List<string> Invalid) Validate(IEnumerable<string>? entries, Normalizer normalize, StringComparer comparer)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            if (!normalize(entry, out var value))
            {
                invalid.Add(entry.Trim());
            }
            else if (seen.Add(value) && valid.Count < MaxEntries)
            {
                valid.Add(value);
            }
        }
        return (valid, invalid);
    }

    public static List<string> CleanDomains(IEnumerable<string>? entries) => Validate(entries, TryNormalizeDomain, StringComparer.Ordinal).Valid;
    public static List<string> CleanNetworks(IEnumerable<string>? entries) => Validate(entries, TryNormalizeNetwork, StringComparer.OrdinalIgnoreCase).Valid;
    public static List<string> CleanApps(IEnumerable<string>? entries) => Validate(entries, TryNormalizeApp, StringComparer.Ordinal).Valid;

    // ── What applies ───────────────────────────────────────────────────────
    /// <summary>App rules need the TUN adapter: in proxy mode only sites are split.</summary>
    public static int EffectiveCount(ColituVpnPreferences preferences)
    {
        preferences = preferences.Normalize();
        if (preferences.SplitTunnelMode == ColituSplitTunnelModes.Off)
        {
            return 0;
        }
        return (preferences.SplitTunnelDomains?.Count ?? 0) + (preferences.SplitTunnelIps?.Count ?? 0)
            + (preferences.IsTunMode ? preferences.SplitTunnelApps?.Count ?? 0 : 0);
    }

    /// <summary>On, with at least one entry that applies in the current mode.</summary>
    public static bool IsActive(ColituVpnPreferences preferences) => EffectiveCount(preferences) > 0;

    public static bool IsIncludeActive(ColituVpnPreferences preferences) =>
        IsActive(preferences) && preferences.Normalize().SplitTunnelMode == ColituSplitTunnelModes.Include;

    public static bool IsExcludeActive(ColituVpnPreferences preferences) =>
        IsActive(preferences) && preferences.Normalize().SplitTunnelMode == ColituSplitTunnelModes.Exclude;

    // ── Rules for the cores ────────────────────────────────────────────────
    /// <summary>
    /// The user's rules, placed before Colitu's regional rules: domains (suffix match),
    /// IPs/CIDRs and, in TUN mode, apps. Exclude mode sends them direct, include mode
    /// through the VPN (see <see cref="BuildCatchAllRule"/> for the rest).
    /// </summary>
    public static List<RulesItem> BuildRules(ColituVpnPreferences preferences)
    {
        preferences = preferences.Normalize();
        var rules = new List<RulesItem>();
        if (!IsActive(preferences))
        {
            return rules;
        }
        var outbound = preferences.SplitTunnelMode == ColituSplitTunnelModes.Exclude ? Global.DirectTag : Global.ProxyTag;
        var verb = outbound == Global.DirectTag ? "bypass the VPN" : "use the VPN";
        if (preferences.SplitTunnelDomains is { Count: > 0 } domains)
        {
            rules.Add(new RulesItem
            {
                Id = "colitu-split-domains",
                Remarks = $"Split tunneling: sites that {verb}",
                OutboundTag = outbound,
                // "domain:" matches the name and its subdomains in Xray and sing-box (domain_suffix).
                Domain = domains.Select(domain => $"domain:{domain}").ToList()
            });
        }
        if (preferences.SplitTunnelIps is { Count: > 0 } ips)
        {
            rules.Add(new RulesItem
            {
                Id = "colitu-split-ips",
                Remarks = $"Split tunneling: addresses that {verb}",
                OutboundTag = outbound,
                Ip = ips.ToList()
            });
        }
        if (preferences.IsTunMode && preferences.SplitTunnelApps is { Count: > 0 } apps)
        {
            rules.Add(new RulesItem
            {
                Id = "colitu-split-apps",
                Remarks = $"Split tunneling: apps that {verb} (sing-box TUN only)",
                OutboundTag = outbound,
                // Only sing-box at the TUN adapter sees which program opened a connection;
                // behind it, Xray would only see sing-box itself.
                InboundTag = [TunInboundTag],
                Process = apps.ToList()
            });
        }
        return rules;
    }

    /// <summary>
    /// Include mode: everything not selected goes direct. In TUN mode the rule is pinned to
    /// the TUN inbound, so Xray (behind sing-box) sends what sing-box handed it through the
    /// VPN. Ports 1-65535 rather than "any" keep v2rayN from switching DNS to direct.
    /// </summary>
    public static RulesItem? BuildCatchAllRule(ColituVpnPreferences preferences)
    {
        preferences = preferences.Normalize();
        if (!IsIncludeActive(preferences))
        {
            return null;
        }
        return new RulesItem
        {
            Id = "colitu-split-rest",
            Remarks = "Split tunneling: everything else goes direct",
            OutboundTag = Global.DirectTag,
            Port = "1-65535",
            Network = "tcp,udp",
            InboundTag = preferences.IsTunMode ? [TunInboundTag] : null
        };
    }

    /// <summary>
    /// Proxy mode, exclude: the desktop sends the excluded sites straight out (GNOME
    /// ignore-hosts, KDE NoProxyFor) on top of <paramref name="baseExceptions"/>.
    /// </summary>
    public static string BuildProxyExceptions(string baseExceptions, ColituVpnPreferences preferences)
    {
        var items = SplitEntries(baseExceptions).ToList();
        preferences = preferences.Normalize();
        if (!preferences.IsTunMode && IsExcludeActive(preferences))
        {
            foreach (var domain in preferences.SplitTunnelDomains ?? [])
            {
                items.Add(domain);
                items.Add($"*.{domain}");
            }
            items.AddRange(preferences.SplitTunnelIps ?? []);
        }
        return string.Join(',', items.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Exclude mode: the addresses the kill switch keeps reachable (they bypass the VPN anyway).</summary>
    public static IReadOnlyList<string> KillSwitchBypassNetworks(ColituVpnPreferences preferences) =>
        IsExcludeActive(preferences) ? preferences.Normalize().SplitTunnelIps ?? [] : [];
}
