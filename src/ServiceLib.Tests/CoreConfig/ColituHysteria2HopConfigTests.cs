namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// The panel adds mport=20000-40000 to hysteria2:// links of nodes that support port hopping
/// (Russian mobile networks throttle a long-lived UDP flow on one port). Both cores must hop:
/// sing-box with server_ports + hop_interval, Xray 26 with finalmask.quicParams.udpHop (the older
/// hysteriaSettings.udphop shape is silently ignored by Xray 26). Without mport nothing changes.
/// </summary>
public class ColituHysteria2HopConfigTests
{
    private const string HopLink = "hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0&mport=20000-40000#Colitu";
    private const string PlainLink = "hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu";

    private static CoreConfigContext Context(ECoreType core, string link)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        // As ColituVpnService sets it: 30 s between hops.
        config.HysteriaItem = new HysteriaItem { UpMbps = 0, DownMbps = 0, HopInterval = 30 };
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = Hysteria2Fmt.Resolve(link, out _)!;
        node.IndexId = "n-hy2";
        node.CoreType = core;
        return CoreConfigTestFactory.CreateContext(config, node, core);
    }

    [Test]
    public async Task ShareLink_WithMport_KeepsThePortRange()
    {
        var node = Hysteria2Fmt.Resolve(HopLink, out _)!;

        await node.Port.Should().BeEqualTo(8443);
        await node.GetProtocolExtra().Ports.Should().BeEqualTo("20000-40000");
    }

    [Test]
    public async Task SingBox_WithMport_HopsOverTheRangeEvery30Seconds()
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, HopLink)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var hysteria = cfg.outbounds.Single(o => o.type == "hysteria2");
        await string.Join(",", hysteria.server_ports ?? []).Should().BeEqualTo("20000:40000");
        await hysteria.hop_interval.Should().BeEqualTo("30s");
        // server_port and server_ports conflict in sing-box.
        await (hysteria.server_port == null).Should().BeTrue();
    }

    [Test]
    public async Task SingBox_WithoutMport_KeepsTheSinglePort()
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, PlainLink)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var hysteria = cfg.outbounds.Single(o => o.type == "hysteria2");
        await (hysteria.server_port == 8443).Should().BeTrue();
        await (hysteria.server_ports == null).Should().BeTrue();
        await (hysteria.hop_interval == null).Should().BeTrue();
    }

    private static JsonNode HysteriaStream(string link, out bool success, out string? msg)
    {
        var result = new CoreConfigV2rayService(Context(ECoreType.Xray, link)).GenerateClientConfigContent();
        success = result.Success;
        msg = result.Msg;
        var root = JsonNode.Parse(result.Data!.ToString()!)!;
        var outbound = root["outbounds"]!.AsArray().Single(o => o?["protocol"]?.GetValue<string>() == "hysteria")!;
        return outbound["streamSettings"]!;
    }

    [Test]
    public async Task Xray_WithMport_HopsThroughFinalmaskQuicParams()
    {
        var stream = HysteriaStream(HopLink, out var success, out var msg);

        await success.Should().BeTrue().Because($"ret msg: {msg}");
        var udpHop = stream["finalmask"]?["quicParams"]?["udpHop"];
        await (udpHop != null).Should().BeTrue();
        await udpHop!["ports"]!.GetValue<string>().Should().BeEqualTo("20000-40000");
        await udpHop["interval"]!.GetValue<string>().Should().BeEqualTo("30");
        // The pre-26 shape is ignored by Xray 26 and must not be relied on.
        await (stream["hysteriaSettings"]?["udphop"] == null).Should().BeTrue();
    }

    [Test]
    public async Task Xray_WithoutMport_HasNoHop()
    {
        var stream = HysteriaStream(PlainLink, out var success, out var msg);

        await success.Should().BeTrue().Because($"ret msg: {msg}");
        await (stream["finalmask"]?["quicParams"]?["udpHop"] == null).Should().BeTrue();
    }
}
