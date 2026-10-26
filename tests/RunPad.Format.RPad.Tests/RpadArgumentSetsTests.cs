using Xunit;

namespace RunPad.Format.RPad.Tests;

// Named argument sets (argumentSets:, selectedArgumentSet:) and which list a run gets (RpadArguments).
public class RpadArgumentSetsTests
{
    private const string WithSets = "---\nversion: \"1.0\"\nargs:\n  - legacy\nargumentSets:\n  smoke:\n    - --verbose\n    - \"file with spaces.txt\"\n    - \"\"\n    - \"say \\\"hi\\\"\"\n  quiet: []\n  bare:\n  \"ünï code\":\n    - é\nselectedArgumentSet: smoke\n---\n```csharp run\n1.Dump();\n```\n";

    private static RpadDocument Parse(string text) => RpadParser.TryParse(text) ?? throw new Xunit.Sdk.XunitException("not parsed");

    [Fact]
    public void Sets_Should_ParseInTheFilesOrder_WithEveryItemAsWritten()
    {
        var fm = Parse(WithSets).FrontMatter;

        Assert.Equal(["smoke", "quiet", "bare", "ünï code"], fm.ArgumentSets.Keys);
        Assert.Equal(["--verbose", "file with spaces.txt", "", "say \"hi\""], fm.ArgumentSets["smoke"]);
        Assert.Empty(fm.ArgumentSets["quiet"]);
        Assert.Empty(fm.ArgumentSets["bare"]); // "bare:" with no value is an empty set
        Assert.Equal(["é"], fm.ArgumentSets["ünï code"]);
        Assert.Equal("smoke", fm.SelectedArgumentSet);
        Assert.Equal(["legacy"], fm.Args);
        Assert.Empty(fm.UnknownEntries);
    }

    [Fact]
    public void Sets_Should_SurviveWriteAndParse()
    {
        var written = RpadWriter.Write(Parse(WithSets));
        var again = Parse(written);

        Assert.Equal(Parse(WithSets).FrontMatter.ArgumentSets, again.FrontMatter.ArgumentSets);
        Assert.Equal("smoke", again.FrontMatter.SelectedArgumentSet);
        Assert.Equal(written, RpadWriter.Write(again));
        Assert.Contains("\n  \"quiet\": []\n", written);
    }

    // A file without sets is written as before: no new keys appear.
    [Fact]
    public void AFileWithoutSets_Should_NotGainTheKeys()
    {
        var written = RpadWriter.Write(Parse("---\nversion: \"1.0\"\nargs:\n  - a\n---\n```csharp run\n1.Dump();\n```\n"));

        Assert.DoesNotContain("argumentSets", written);
        Assert.DoesNotContain("selectedArgumentSet", written);
    }

    [Fact]
    public void ADuplicateSetName_Should_BeAParseError()
    {
        var result = RpadParser.Parse("---\nargumentSets:\n  a: []\n  a: [x]\n---\n```csharp run\n1.Dump();\n```\n");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Snapshot_Should_CopyTheSets()
    {
        var doc = Parse(WithSets);
        var restored = RpadDocument.FromSnapshot(doc.CaptureSnapshot());
        doc.FrontMatter.ArgumentSets["smoke"].Add("later");

        Assert.Equal(["--verbose", "file with spaces.txt", "", "say \"hi\""], restored.FrontMatter.ArgumentSets["smoke"]);
        Assert.Equal("smoke", restored.FrontMatter.SelectedArgumentSet);
    }

    // ── Which arguments a run gets ─────────────────────────────────────────────

    [Fact]
    public void Resolve_Should_PreferGiven_ThenNamed_ThenSelected_ThenArgs()
    {
        var fm = Parse(WithSets).FrontMatter;

        Assert.Equal(["cli"], RpadArguments.Resolve(fm, ["cli"], "quiet").Arguments);
        Assert.Empty(RpadArguments.Resolve(fm, [], "quiet").Arguments); // given but empty: none at all
        Assert.Empty(RpadArguments.Resolve(fm, null, "quiet").Arguments);
        Assert.Equal(4, RpadArguments.Resolve(fm).Arguments.Count);
        fm.SelectedArgumentSet = null;
        Assert.Equal(["legacy"], RpadArguments.Resolve(fm).Arguments);
        Assert.Empty(RpadArguments.Resolve(null).Arguments);
    }

    [Fact]
    public void Resolve_Should_RefuseAnUnknownSet_InsteadOfFallingBack()
    {
        var fm = Parse(WithSets).FrontMatter;

        var named = RpadArguments.Resolve(fm, null, "nightly");
        fm.SelectedArgumentSet = "gone";
        var selected = RpadArguments.Resolve(fm);
        var none = RpadArguments.Resolve(new RpadFrontMatter { SelectedArgumentSet = "x", Args = ["legacy"] });

        Assert.Empty(named.Arguments);
        Assert.Contains("'nightly'", named.Error);
        Assert.Contains("smoke, quiet, bare, ünï code", named.Error);
        Assert.Contains("'gone'", selected.Error);
        Assert.Empty(none.Arguments);
        Assert.Contains("defines none", none.Error);
        Assert.Null(RpadArguments.Resolve(fm, ["given"]).Error); // given arguments need no set
    }

    // Names are compared exactly: "Smoke" is not "smoke".
    [Fact]
    public void Resolve_Should_MatchNamesExactly()
        => Assert.NotNull(RpadArguments.Resolve(Parse(WithSets).FrontMatter, null, "Smoke").Error);
}
