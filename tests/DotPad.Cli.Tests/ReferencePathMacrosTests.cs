using DotPad.Languages.DotnetCommon;
using Xunit;

namespace DotPad.Cli.Tests;

// TODO.md #63: focused tests for the macro-expansion logic itself, separate from
// CliRunnerTests.cs's own coverage of BuildOptions actually calling into it.
public class ReferencePathMacrosTests
{
    [Fact]
    public void Expand_Should_ReplaceMyDocuments()
    {
        var expanded = ReferencePathMacros.Expand("$(MyDocuments)/Shared/Foo.dll", currentQueryPath: null);

        Assert.DoesNotContain("$(MyDocuments)", expanded);
        Assert.EndsWith("/Shared/Foo.dll", expanded);
    }

    [Fact]
    public void Expand_Should_ReplaceCurrentQueryPath_WithTheContainingDirectory_NotTheFilePathItself()
    {
        var expanded = ReferencePathMacros.Expand(
            "$(CurrentQueryPath)/Lib.dll", currentQueryPath: "/home/user/scripts/query.rpad");

        Assert.Equal("/home/user/scripts/Lib.dll", expanded);
    }

    [Fact]
    public void Expand_Should_LeaveReferenceUnchanged_WhenItHasNoMacros()
    {
        var expanded = ReferencePathMacros.Expand("/absolute/path/Foo.dll", currentQueryPath: "/some/query.rpad");

        Assert.Equal("/absolute/path/Foo.dll", expanded);
    }

    [Fact]
    public void Expand_Should_ExpandMultipleMacrosInTheSameReference()
    {
        var expanded = ReferencePathMacros.Expand(
            "$(CurrentQueryPath)/../$(MyDocuments)/Foo.dll", currentQueryPath: "/home/user/scripts/query.rpad");

        Assert.DoesNotContain("$(CurrentQueryPath)", expanded);
        Assert.DoesNotContain("$(MyDocuments)", expanded);
        Assert.StartsWith("/home/user/scripts/../", expanded);
    }
}
