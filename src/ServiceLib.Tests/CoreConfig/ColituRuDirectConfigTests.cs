namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Colitu sends Russian domains (geosite:category-ru) and Russian IPs (geoip:ru) out directly
/// (ColituVPN/Services/ColituVpnService.cs, BuildColituRoutingRules) with IPIfNonMatch routing.
/// These tests pin down that both cores turn those rules into direct routes.
/// </summary>
public class ColituRuDirectConfigTests
{
    private static CoreConfigContext RuDirectContext(ECoreType core)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.RoutingBasicItem.DomainStrategy = Global.IPIfNonMatch;
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
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.ProxyTag,
                        Port = "53",
                        Network = "tcp,udp",
                    },
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:category-ru"],
                    },
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Ip = ["geoip:ru"],
                    }
                }),
                DomainStrategy = Global.IPIfNonMatch,
                DomainStrategy4Singbox = string.Empty,
            }
        };
    }

    [Test]
    public async Task Xray_SendsRussianDomainsAndIpsDirect()
    {
        var result = new CoreConfigV2rayService(RuDirectContext(ECoreType.Xray)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        await cfg.routing.domainStrategy.Should().BeEqualTo(Global.IPIfNonMatch);
        await cfg.routing.rules.Should().Contain(r => r.domain != null && r.domain.Contains("geosite:category-ru") && r.outboundTag == Global.DirectTag);
        await cfg.routing.rules.Should().Contain(r => r.ip != null && r.ip.Contains("geoip:ru") && r.outboundTag == Global.DirectTag);
    }

    [Test]
    public async Task Xray_LooksForGeoFilesWhereThePackageInstallsThem()
    {
        // scripts/package-linux.sh installs the core bundle's geoip.dat/geosite.dat in bin/ (not next
        // to xray in bin/xray, as on Windows): with any other asset path every geosite: rule fails.
        var xray = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray)!;

        await Path.GetFullPath(xray.Environment[Global.XrayLocalAsset]!).TrimEnd(Path.DirectorySeparatorChar)
            .Should().BeEqualTo(Path.GetFullPath(Utils.GetBinPath("")).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task SingBox_UsesTheRuleSetsAndResolvesBeforeTheIpRule()
    {
        var result = new CoreConfigSingboxService(RuDirectContext(ECoreType.sing_box)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        await cfg.route.rule_set.Should().Contain(r => r.tag == "geosite-category-ru");
        await cfg.route.rule_set.Should().Contain(r => r.tag == "geoip-ru");

        var rules = cfg.route.rules;
        await rules.Should().Contain(r => r.rule_set != null && r.rule_set.Contains("geosite-category-ru") && r.outbound == Global.DirectTag);
        var resolve = rules.FindIndex(r => r.action == "resolve");
        await resolve.Should().BeGreaterThanOrEqualTo(0).Because("a domain must be resolved before the Russian IP rule can match");
        await rules.FindIndex(resolve + 1, r => r.rule_set != null && r.rule_set.Contains("geoip-ru") && r.outbound == Global.DirectTag)
            .Should().BeGreaterThan(resolve);
    }
}
