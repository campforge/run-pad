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
        // Built via Path.Combine/GetDirectoryName rather than a hardcoded "/home/user/..." literal —
        // GetDirectoryName normalizes to the platform's own separator (backslashes on Windows), so a
        // forward-slash-only expected string here would fail on Windows CI while passing on Linux/Mac.
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");
        var expectedDir = Path.GetDirectoryName(currentQueryPath);

        var expanded = ReferencePathMacros.Expand("$(CurrentQueryPath)/Lib.dll", currentQueryPath);

        Assert.Equal(expectedDir + "/Lib.dll", expanded);
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
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");
        var expectedDir = Path.GetDirectoryName(currentQueryPath);

        var expanded = ReferencePathMacros.Expand(
            "$(CurrentQueryPath)/../$(MyDocuments)/Foo.dll", currentQueryPath);

        Assert.DoesNotContain("$(CurrentQueryPath)", expanded);
        Assert.DoesNotContain("$(MyDocuments)", expanded);
        Assert.StartsWith(expectedDir + "/../", expanded);
    }
}
