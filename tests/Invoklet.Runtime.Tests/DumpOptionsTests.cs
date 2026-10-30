using Xunit;

namespace Invoklet.Runtime.Tests;

// LINQPad Runtime API compatibility — DumpOptionsCore/DumpOptions
// shape verified against the real LINQPad.Runtime.dll. CombineWith's tested here as a pure function
// on freshly-constructed instances only — never against the actual process-wide DumpOptions.Default/
// Util.DumpDefaults singleton, which every Render() call reads (DumpRenderer.Render), making it
// process-wide mutable state: a test that mutated it without perfect isolation could flakily affect
// any other test's rendering assertions run concurrently in the same process (xunit parallelizes
// across test classes by default). CombineWith's own logic is fully exercised without touching that
// shared instance at all.
public class DumpOptionsTests
{
    [Fact]
    public void UtilDumpDefaults_And_DumpOptionsDefault_AreTheSameInstance()
    {
        Assert.Same(Util.DumpDefaults, DumpOptions.Default);
    }

    [Fact]
    public void CombineWith_NullablePropertiesOnOther_FallBackToThis_WhenUnset()
    {
        var baseOptions = new DumpOptions { MaxDepth = 5, CollapseTo = 2 };
        var overrides = new DumpOptions(); // every nullable property left null

        var combined = baseOptions.CombineWith(overrides);

        Assert.Equal(5, combined.MaxDepth);
        Assert.Equal(2, combined.CollapseTo);
    }

    [Fact]
    public void CombineWith_NullablePropertiesOnOther_WinOverThis_WhenSet()
    {
        var baseOptions = new DumpOptions { MaxDepth = 5 };
        var overrides = new DumpOptions { MaxDepth = 1 };

        var combined = baseOptions.CombineWith(overrides);

        Assert.Equal(1, combined.MaxDepth);
    }

    [Fact]
    public void CombineWith_StringPropertiesOnOther_FallBackToThis_WhenUnset()
    {
        var baseOptions = new DumpOptions { Description = "base", MembersToInclude = "A,B" };
        var overrides = new DumpOptions();

        var combined = baseOptions.CombineWith(overrides);

        Assert.Equal("base", combined.Description);
        Assert.Equal("A,B", combined.MembersToInclude);
    }

    [Fact]
    public void CombineWith_BoolPropertiesOnOther_AlwaysWin_EvenWhenFalse()
    {
        // ToDataGrid/NoTotals/IncludePrivateMembers/ForceExpansion have no "unset" tri-state (unlike
        // the nullable properties above) — CombineWith always takes other's plain bool value.
        var baseOptions = new DumpOptions { ToDataGrid = true, ForceExpansion = true };
        var overrides = new DumpOptions(); // ToDataGrid/ForceExpansion default to false

        var combined = baseOptions.CombineWith(overrides);

        Assert.False(combined.ToDataGrid);
        Assert.False(combined.ForceExpansion);
    }

    [Fact]
    public void CombineWith_FormatStrings_AlwaysTakesOther()
    {
        var baseOptions = new DumpOptions { FormatStrings = new DumpFormatStrings { IntegralNumber = "N0" } };
        var overrideFormats = new DumpFormatStrings();
        var overrides = new DumpOptions { FormatStrings = overrideFormats };

        var combined = baseOptions.CombineWith(overrides);

        Assert.Same(overrideFormats, combined.FormatStrings);
    }

    [Fact]
    public void DumpOptions_Constructors_MatchTheRealLinqPadShapes()
    {
        Assert.Null(new DumpOptions().Description);
        Assert.Equal("desc", new DumpOptions("desc").Description);
        Assert.True(new DumpOptions(true).ToDataGrid);
        Assert.Equal("desc2", new DumpOptions(true, "desc2").Description);
    }

    [Fact]
    public void DumpFormatStrings_EveryProperty_DefaultsToNull()
    {
        var format = new DumpFormatStrings();

        Assert.Null(format.IntegralNumber);
        Assert.Null(format.RealNumber);
        Assert.Null(format.DateTime);
        Assert.Null(format.DateTimeOffset);
        Assert.Null(format.DateOnly);
        Assert.Null(format.TimeOnly);
    }
}
