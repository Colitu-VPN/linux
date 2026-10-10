namespace ServiceLib.Tests.Handler;

public class AutoStartupHandlerTests
{
    [Test]
    public async Task DesktopExec_PackagedPathIsQuoted()
    {
        await AutoStartupHandler.DesktopExecQuote("/opt/colitu-vpn/ColituVPN").Should().BeEqualTo("\"/opt/colitu-vpn/ColituVPN\"");
    }

    [Test]
    public async Task DesktopExec_SpacesAndReservedCharactersSurvive()
    {
        await AutoStartupHandler.DesktopExecQuote("/home/u/My Apps/Colitu VPN/ColituVPN")
            .Should().BeEqualTo("\"/home/u/My Apps/Colitu VPN/ColituVPN\"");
        await AutoStartupHandler.DesktopExecQuote("/home/u/a$b`c\"d%e\\f/ColituVPN")
            .Should().BeEqualTo("\"/home/u/a\\$b\\`c\\\"d%%e\\\\\\\\f/ColituVPN\"");
    }
}
