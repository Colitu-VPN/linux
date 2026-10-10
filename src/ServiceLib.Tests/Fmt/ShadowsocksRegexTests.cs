using System.Diagnostics;

namespace ServiceLib.Tests.Fmt;

public class ShadowsocksRegexTests
{
    /// <summary>
    /// A legacy ss:// link whose decoded text is full of ':' and '@' made the details regex backtrack
    /// cubically (one hostile subscription line could hang the import): it now times out and the link is refused.
    /// </summary>
    [Test]
    public async Task LegacyLinkWithHostileText_IsRefusedWithinTheTimeLimit()
    {
        var hostile = "aes-256-gcm" + string.Concat(Enumerable.Repeat(":@", 4000)) + "zz";
        var uri = "ss://" + Utils.Base64Encode(hostile, true);

        var watch = Stopwatch.StartNew();
        var resolved = FmtHandler.ResolveConfig(uri, out _);
        watch.Stop();

        await resolved.Should().BeNull();
        await watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task LegacyLink_StillResolvesNormally()
    {
        var uri = "ss://" + Utils.Base64Encode("aes-256-gcm:pa:ss@host.example:8388", true) + "#demo";

        var resolved = FmtHandler.ResolveConfig(uri, out _);

        await resolved.Should().NotBeNull();
        await resolved!.Address.Should().BeEqualTo("host.example");
        await resolved.Port.Should().BeEqualTo(8388);
        await resolved.Password.Should().BeEqualTo("pa:ss");
    }
}
