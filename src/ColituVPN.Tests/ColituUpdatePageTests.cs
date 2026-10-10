using System;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// "Update now" on an installation that cannot update itself (tarball, arm64) opens the download page.
/// Up to 1.4.4 that was https://colitu.com/downloads/linux, the package folder, which the web server
/// redirected to its internal port 7443 ("site can't be reached").
/// </summary>
public class ColituUpdatePageTests
{
    [Fact]
    public void DownloadPage_IsThePublicPage_NotThePackageFolder()
    {
        var uri = new Uri(ColituUpdateService.DownloadPageUrl);

        uri.Scheme.Should().Be(Uri.UriSchemeHttps);
        uri.Host.Should().Be("colitu.com");
        uri.IsDefaultPort.Should().BeTrue();
        uri.AbsolutePath.Should().Be("/download/linux");
        uri.AbsolutePath.Should().NotStartWith("/downloads/");
    }

    [Fact]
    public void DownloadPage_IsALinkTheAppWillOpen()
    {
        ColituShell.IsSafeLink(ColituUpdateService.DownloadPageUrl, allowMail: false).Should().BeTrue();
    }
}
