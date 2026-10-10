namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Colitu split tunneling (ColituVPN/Services/ColituSplitTunnel.cs) writes these rule shapes;
/// ColituVPN.Tests pins the shapes on that side. These tests pin how both cores turn them
/// into routes: domains as suffixes, IPs as CIDRs, apps only at sing-box's TUN inbound, and
/// the include-mode "everything else direct" rule.
/// </summary>
public class ColituSplitTunnelConfigTests
{
    private static List<RulesItem> Rules(string outbound, bool tun, bool include) =>
    [
        new()
        {
            Id = "colitu-split-domains", Enabled = true, OutboundTag = outbound,
            Domain = ["domain:example.com", "domain:xn--e1afmkfd.xn--p1ai"],
        },
        new()
        {
            Id = "colitu-split-ips", Enabled = true, OutboundTag = outbound,
            Ip = ["203.0.113.0/24", "2001:db8::/32", "198.51.100.7"],
        },
        .. tun
            ? new List<RulesItem>
            {
                new()
                {
                    Id = "colitu-split-apps", Enabled = true, OutboundTag = outbound,
                    InboundTag = ["tun"], Process = ["firefox", "/opt/telegram/Telegram"],
                },
            }
            : [],
        .. include
            ? new List<RulesItem>
            {
                new()
                {
                    Id = "colitu-split-rest", Enabled = true, OutboundTag = Global.DirectTag,
                    Port = "1-65535", Network = "tcp,udp", InboundTag = tun ? ["tun"] : null,
                },
            }
            : [],
    ];

    private static CoreConfigContext Context(ECoreType core, string outbound, bool tun, bool include)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.RoutingBasicItem.DomainStrategy = Global.IPIfNonMatch;
        config.TunModeItem.EnableTun = tun;
        config.TunModeItem.AutoRoute = tun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "colitu",
                Remarks = "Colitu",
                RuleSet = JsonUtils.Serialize(Rules(outbound, tun, include)),
                DomainStrategy = Global.IPIfNonMatch,
                DomainStrategy4Singbox = string.Empty,
            },
        };
    }

    private static SingboxConfig SingBox(string outbound, bool tun, bool include)
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, outbound, tun, include)).GenerateClientConfigContent();
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Msg);
        }
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }

    private static V2rayConfig Xray(string outbound, bool tun, bool include)
    {
        var result = new CoreConfigV2rayService(Context(ECoreType.Xray, outbound, tun, include)).GenerateClientConfigContent();
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Msg);
        }
        return JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
    }

    [Test]
    public async Task SingBox_Tun_Exclude_SendsSitesAddressesAndAppsDirect()
    {
        var rules = SingBox(Global.DirectTag, tun: true, include: false).route.rules;

        await rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("example.com") && r.outbound == Global.DirectTag);
        await rules.Should().Contain(r => r.ip_cidr != null && r.ip_cidr.Contains("203.0.113.0/24") && r.ip_cidr.Contains("2001:db8::/32") && r.outbound == Global.DirectTag);
        await rules.Should().Contain(r => r.process_name != null && r.process_name.Contains(Utils.GetExeName("firefox"))
            && r.inbound != null && r.inbound.Contains("tun") && r.outbound == Global.DirectTag);
        await rules.Should().Contain(r => r.process_path != null && r.process_path.Contains("/opt/telegram/Telegram".Replace('/', Path.DirectorySeparatorChar))
            && r.inbound != null && r.inbound.Contains("tun") && r.outbound == Global.DirectTag);
    }

    [Test]
    public async Task SingBox_Tun_Include_SendsTheRestDirectAtTheTunOnly()
    {
        var cfg = SingBox(Global.ProxyTag, tun: true, include: true);
        var rules = cfg.route.rules;

        await rules.Should().Contain(r => r.process_name != null && r.process_name.Contains(Utils.GetExeName("firefox")) && r.outbound == Global.ProxyTag);
        var rest = rules.FindIndex(r => r.port_range != null && r.port_range.Contains("1:65535") && r.outbound == Global.DirectTag);
        await rest.Should().BeGreaterThanOrEqualTo(0);
        await rules[rest].inbound.Should().Contain("tun");
        await rules.FindIndex(r => r.domain_suffix != null && r.domain_suffix.Contains("example.com")).Should().BeLessThan(rest);
        await cfg.route.final.Should().BeEqualTo(Global.ProxyTag);
    }

    [Test]
    public async Task SingBox_Tun_Include_SelectedAppsResolveThroughTheTunnel()
    {
        var cfg = SingBox(Global.ProxyTag, tun: true, include: true);

        var remote = cfg.dns.servers.Single(s => s.tag == Global.SingboxRemoteDNSTag);
        await remote.detour.Should().BeEqualTo(Global.ProxyTag);
        var byName = cfg.dns.rules.FindIndex(r => r.process_name != null && r.process_name.Contains(Utils.GetExeName("firefox")) && r.server == Global.SingboxRemoteDNSTag);
        var byPath = cfg.dns.rules.FindIndex(r => r.process_path != null && r.process_path.Contains("/opt/telegram/Telegram".Replace('/', Path.DirectorySeparatorChar)) && r.server == Global.SingboxRemoteDNSTag);
        await byName.Should().BeGreaterThanOrEqualTo(0);
        await byPath.Should().BeGreaterThanOrEqualTo(0);
        // No rule that answers from a direct resolver sits in front of them (the clash-mode switch and the
        // protected server names are not about these programs).
        await cfg.dns.rules.Take(Math.Min(byName, byPath)).Any(r => r.server?.StartsWith(Global.SingboxDirectDNSTagPrefix) == true && r.clash_mode == null && r.domain == null).Should().BeFalse();
    }

    [Test]
    public async Task SingBox_Tun_Exclude_AddsNoRemoteDnsRulesForTheApps()
    {
        var cfg = SingBox(Global.DirectTag, tun: true, include: false);

        await cfg.dns.rules.Any(r => (r.process_path != null || r.process_name != null) && r.server == Global.SingboxRemoteDNSTag).Should().BeFalse();
    }

    [Test]
    public async Task Xray_Proxy_Exclude_SendsSitesAndAddressesDirect()
    {
        var rules = Xray(Global.DirectTag, tun: false, include: false).routing.rules;

        await rules.Should().Contain(r => r.domain != null && r.domain.Contains("domain:example.com") && r.outboundTag == Global.DirectTag);
        await rules.Should().Contain(r => r.ip != null && r.ip.Contains("203.0.113.0/24") && r.outboundTag == Global.DirectTag);
        await rules.Any(r => r.process != null && r.process.Count > 0).Should().BeFalse();
    }

    [Test]
    public async Task Xray_Proxy_Include_SendsTheRestDirect()
    {
        var rules = Xray(Global.ProxyTag, tun: false, include: true).routing.rules;

        await rules.Should().Contain(r => r.domain != null && r.domain.Contains("domain:example.com") && r.outboundTag == Global.ProxyTag);
        await rules.Should().Contain(r => r.port == "1-65535" && r.network == "tcp,udp" && r.outboundTag == Global.DirectTag && r.inboundTag == null);
    }

    [Test]
    public async Task Xray_BehindTheTun_IgnoresTheTunOnlyRules()
    {
        // In TUN mode Xray only gets what sing-box sent through the VPN: its copies of the app
        // and "everything else" rules are tied to the "tun" inbound, which Xray does not have.
        var rules = Xray(Global.ProxyTag, tun: true, include: true).routing.rules;

        // (v2rayN's own TUN rules for the cores' executables are not Colitu's and stay as they are.)
        var tunOnly = rules.Where(r => r.port == "1-65535" || (r.process != null && r.process.Contains("firefox"))).ToList();
        await tunOnly.Count.Should().BeGreaterThan(0);
        await tunOnly.All(r => r.inboundTag != null && r.inboundTag.Contains("tun")).Should().BeTrue();
    }
}
