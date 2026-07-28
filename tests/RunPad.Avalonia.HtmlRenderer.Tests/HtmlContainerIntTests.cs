using RunPad.Avalonia.HtmlRenderer.External.Core;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

// TODO.md #46: clicking a link in .Dump() HTML output used to call Process.Start(UseShellExecute:
// true) on whatever HrefLink the content contained, with no check on the URI scheme. Fixed with an
// http(s)-only allowlist — these tests exercise that check directly, since HandleLinkClicked itself
// needs a full CssBox/RControl DOM to call and would actually launch a process if it ran unfiltered.
public class HtmlContainerIntTests
{
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/path?query=1")]
    public void TryGetSafeExternalLink_Should_Allow_HttpAndHttps(string href)
    {
        var result = HtmlContainerInt.TryGetSafeExternalLink(href, out var uri);

        Assert.True(result);
        Assert.Equal(href, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("custom-app-protocol://payload")]
    [InlineData("not a uri at all")]
    [InlineData("")]
    public void TryGetSafeExternalLink_Should_Reject_NonHttpSchemes(string href)
    {
        var result = HtmlContainerInt.TryGetSafeExternalLink(href, out var uri);

        Assert.False(result);
        Assert.Null(uri);
    }
}
