using Xunit;

namespace DotPad.Format.RPad.Tests;

public class RpadParserWriterTests
{
    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void Write_ThenParse_RoundTripsKind(RpadKind kind)
    {
        var doc = new RpadDocument
        {
            FrontMatter = new RpadFrontMatter { TargetSdk = new TargetSdk("net", "8.0"), Kind = kind },
            RunCode = "Console.WriteLine(1);\n",
        };

        var text = RpadWriter.Write(doc);
        var parsed = RpadParser.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.FrontMatter.Kind);
        Assert.Equal(RpadLanguage.CSharp, parsed.FrontMatter.Language);
        Assert.Equal("8.0", parsed.FrontMatter.TargetSdk.Version);
        Assert.Equal("net", parsed.FrontMatter.TargetSdk.Name);
    }

    [Fact]
    public void Write_ThenParse_RoundTripsUsingsPackagesReferences()
    {
        var doc = new RpadDocument
        {
            FrontMatter = new RpadFrontMatter
            {
                Namespaces = ["System.Text", "System.Linq"],
                Packages = new Dictionary<string, Package> { ["Dapper"] = new("2.1.66") },
                References = ["../Shared/Shared.csproj"],
            },
            RunCode = "1.Dump();\n",
        };

        var parsed = RpadParser.TryParse(RpadWriter.Write(doc));

        Assert.NotNull(parsed);
        Assert.Equal(["System.Text", "System.Linq"], parsed!.FrontMatter.Namespaces);
        Assert.Equal("2.1.66", parsed.FrontMatter.Packages?["Dapper"].Version);
        Assert.Equal(["../Shared/Shared.csproj"], parsed.FrontMatter.References);
    }

    // ── NewFile ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void NewFile_ThenParse_ProducesRequestedKind(RpadKind kind)
    {
        var text = RpadWriter.NewFile("Untitled", "net10.0", kind);

        var parsed = RpadParser.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.FrontMatter.Kind);
    }

    // ── Malformed / missing values ────────────────────────────────────────────

    [Fact]
    public void TryParse_EmptyKindValue_FallsBackToDefaultWithoutThrowing()
    {
        const string content =
            "---\nversion: 1\nlanguage: csharp\ntargetSdk: net10.0\nkind:\n---\n\n```csharp run\n1.Dump();\n```\n";

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
    }

    [Fact]
    public void TryParse_InvalidKindValue_FallsBackToFreshFrontMatter()
    {
        const string content =
            "---\nversion: 1\nlanguage: csharp\ntargetSdk: net10.0\nkind: NotARealKind\npackages:\n  Dapper: 2.1.66\n---\n\n```csharp run\n1.Dump();\n```\n";

        var parsed = RpadParser.TryParse(content);

        // The whole front matter resets to defaults on an unparseable enum value (YamlDotNet throws,
        // caught by TryParse's outer catch) — packages included. See ANALYSIS.md #37 for the tradeoff.
        Assert.NotNull(parsed);
        Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
        Assert.Null(parsed.FrontMatter.Packages);
    }

    [Fact]
    public void TryParse_MissingTargetSdkValue_FallsBackToDefaultWithoutThrowing()
    {
        const string content =
            "---\nversion: 1\nlanguage: csharp\ntargetSdk:\nkind: Statements\n---\n\n```csharp run\n1.Dump();\n```\n";

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal("net", parsed!.FrontMatter.TargetSdk.Name);
        Assert.Equal("10.0", parsed!.FrontMatter.TargetSdk.Version);
    }
}
