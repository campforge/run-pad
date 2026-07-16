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
    public void Write_ThenParse_RoundTripsNamespacesPackagesReferences()
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

    [Fact]
    public void TryParse_ReadmeFrontMatterExample_ParsesAllFieldsWithoutResetting()
    {
        // Mirrors the front-matter example from src/core/DotPad.Format.RPad/README.md verbatim.
        // Regression guard: TargetSdk/Package are plain YAML scalars, and without the type
        // converters registered on the deserializer, YamlDotNet fails to bind them onto the
        // TargetSdk/Package records and throws — which silently resets the *entire* front matter
        // (including Kind and Packages) back to defaults instead of just falling back for the one
        // unparseable field.
        const string content = """
            ---
            version: "1.0"
            language: csharp
            targetSdk: latest
            kind: Statements
            packages:
              Dapper: 2.1.66
            references:
              - ../Shared/Shared.csproj
            namespaces:
              - System
              - System.Linq
            ---

            ```csharp run
            Console.WriteLine("Hello");
            ```
            """;

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
        Assert.Equal(RpadLanguage.CSharp, parsed.FrontMatter.Language);
        Assert.Equal(TargetSdk.Default, parsed.FrontMatter.TargetSdk);
        Assert.Equal("2.1.66", parsed.FrontMatter.Packages["Dapper"].Version);
        Assert.Equal(["../Shared/Shared.csproj"], parsed.FrontMatter.References);
        Assert.Equal(["System", "System.Linq"], parsed.FrontMatter.Namespaces);
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

    // TODO.md #49 item 2 — NewFile used to hardcode `language: csharp` and the ```csharp fence
    // regardless of caller intent, so a new .vb file could only be made by hand-editing an existing
    // C# file's front matter afterward.
    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void NewFile_WithVbLanguage_ThenParse_ProducesVbLanguageAndFence(RpadKind kind)
    {
        var text = RpadWriter.NewFile("Untitled", "net10.0", kind, RpadLanguage.Vb);

        Assert.Contains("```vb run", text);
        var parsed = RpadParser.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(RpadLanguage.Vb, parsed!.FrontMatter.Language);
        Assert.Equal(kind, parsed.FrontMatter.Kind);
    }

    [Fact]
    public void NewFile_DefaultsToCSharp_UnchangedFromBeforeTheLanguageParameterExisted()
    {
        var text = RpadWriter.NewFile("Untitled", "net10.0", RpadKind.Statements);

        Assert.Contains("language: csharp", text);
        Assert.Contains("```csharp run", text);
        Assert.Contains("Console.WriteLine(\"Hello, RPad!\");", text);
    }

    // VB has no statement-terminating semicolon — a C#-shaped default snippet would be a syntax
    // error the very first time a new VB file is run.
    [Fact]
    public void NewFile_WithVbLanguage_StatementsDefault_HasNoTrailingSemicolon()
    {
        var text = RpadWriter.NewFile("Untitled", "net10.0", RpadKind.Statements, RpadLanguage.Vb);

        Assert.Contains("Console.WriteLine(\"Hello, RPad!\")\n", text);
        Assert.DoesNotContain("Console.WriteLine(\"Hello, RPad!\");", text);
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
        // Packages is a non-nullable dictionary defaulting to empty, not null.
        Assert.NotNull(parsed);
        Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
        Assert.Empty(parsed.FrontMatter.Packages);
    }

    [Fact]
    public void TryParse_MissingTargetSdkValue_FallsBackToDefaultWithoutThrowing()
    {
        const string content =
            "---\nversion: 1\nlanguage: csharp\ntargetSdk:\nkind: Statements\n---\n\n```csharp run\n1.Dump();\n```\n";

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(TargetSdk.Default, parsed!.FrontMatter.TargetSdk);
    }
}
