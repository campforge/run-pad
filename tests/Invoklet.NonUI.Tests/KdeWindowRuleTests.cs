using Invoklet.Desktop.Linux;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class KdeWindowRuleTests
{
    private const string UserRules =
        "[General]\ncount=1\nrules=abc\n\n[abc]\nDescription=Firefox\nwmclass=firefox\n";

    [Fact]
    public void AddRule_ToEmptyFile_CreatesGeneralAndRule()
    {
        var text = KdeWindowRule.AddRule("");

        Assert.True(KdeWindowRule.HasRule(text));
        Assert.Contains("count=2\n", text);
        Assert.Contains("positionrule=4", text);
        Assert.Contains("wmclass=Invoklet", text);
    }

    [Fact]
    public void AddRule_KeepsUserRules()
    {
        var text = KdeWindowRule.AddRule(UserRules);

        Assert.Contains($"rules=abc,{KdeWindowRule.Group},{KdeWindowRule.DialogGroup}\n", text);
        Assert.Contains("count=3\n", text);
        Assert.Contains("[abc]\nDescription=Firefox\nwmclass=firefox\n", text);
    }

    [Fact]
    public void AddRule_Twice_AddsItOnce()
    {
        var once = KdeWindowRule.AddRule(UserRules);

        Assert.Equal(once, KdeWindowRule.AddRule(once));
    }

    [Fact]
    public void AddRule_PlasmaSixEmptyList_Works()
    {
        var text = KdeWindowRule.AddRule("[General]\nrules=\n");

        Assert.Contains($"rules={KdeWindowRule.Group},{KdeWindowRule.DialogGroup}\n", text);
        Assert.True(KdeWindowRule.HasRule(text));
    }

    [Fact]
    public void AddRule_RemembersOnlyTheMainWindow_AndCentersEveryOtherWindow()
    {
        var text = KdeWindowRule.AddRule("");

        Assert.Contains($"rules={KdeWindowRule.Group},{KdeWindowRule.DialogGroup}\n", text);
        Assert.Contains("title=Invoklet\ntitlematch=1\n", text);
        Assert.Contains($"[{KdeWindowRule.DialogGroup}]\n", text);
        Assert.Contains("placement=5\nplacementrule=2\ntitle=^(?!Invoklet$)\ntitlematch=3\n", text);
    }

    [Fact]
    public void AddRule_RewritesARuleFromAnOlderVersion()
    {
        var old = "[General]\ncount=1\nrules=Invoklet-remember-position\n\n[Invoklet-remember-position]\n" +
            "Description=Invoklet: remember window position\nposition=10,10\npositionrule=4\nwmclass=Invoklet\nwmclassmatch=1\n";

        var text = KdeWindowRule.AddRule(old);

        Assert.Single(text.Split('\n'), l => l == $"[{KdeWindowRule.Group}]");
        Assert.Contains("titlematch=1\n", text);
        Assert.Contains($"rules={KdeWindowRule.Group},{KdeWindowRule.DialogGroup}\n", text);
        Assert.Contains("count=2\n", text);
    }

    [Fact]
    public void RemoveRule_RestoresUserRules()
    {
        var text = KdeWindowRule.RemoveRule(KdeWindowRule.AddRule(UserRules));

        Assert.False(KdeWindowRule.HasRule(text));
        Assert.DoesNotContain(KdeWindowRule.Group, text);
        Assert.DoesNotContain(KdeWindowRule.DialogGroup, text);
        Assert.Contains("rules=abc\n", text);
        Assert.Contains("count=1\n", text);
        Assert.Contains("[abc]\nDescription=Firefox\nwmclass=firefox", text);
    }

    [Fact]
    public void RemoveRule_WithoutRule_LeavesFileUnchanged()
    {
        Assert.Equal(UserRules, KdeWindowRule.RemoveRule(UserRules));
    }
}
