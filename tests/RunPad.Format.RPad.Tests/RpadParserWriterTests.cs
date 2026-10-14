using Xunit;

namespace RunPad.Format.RPad.Tests;

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
        // A document built in code has no Markdown body — Write must still fence the code.
        Assert.Equal("Console.WriteLine(1);\n", parsed.RunCode);
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
        // Mirrors the front-matter example from src/core/RunPad.Format.RPad/README.md verbatim.
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

    // A brand-new, never-saved tab wants genuinely empty code
    // (not the "Hello, RPad!" sample below), via NewFile's optional code override. An empty code
    // block glues the closing fence onto the same line as the (empty) code instead of its own line —
    // RpadParser.FindClosingFence requires a closing fence to start its own line, so without
    // RpadWriter.NewFile normalizing a trailing newline the same way Write() already does for
    // RunCode, this used to parse back with a mangled RunCode containing the literal closing-fence
    // text and an unclosed fence in BodyAfterCode — i.e. genuinely invalid Markdown once saved back
    // out via RpadWriter.Write, not just a cosmetic parsing quirk.
    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void NewFile_WithEmptyCodeOverride_ThenParse_RoundTripsCleanlyThroughWrite(RpadKind kind)
    {
        var text = RpadWriter.NewFile("Untitled", "net10.0", kind, code: "");

        var parsed = RpadParser.TryParse(text);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.FrontMatter.Kind);
        Assert.DoesNotContain('`', parsed.RunCode);

        // The real-world path: SetDocumentContent parses this once, the user never types anything,
        // and the tab gets closed/saved as-is — WriteFile always overwrites RunCode with whatever's
        // in the editor first, so simulate that explicitly rather than relying on the parsed
        // (possibly already-empty) RunCode.
        parsed.RunCode = "";
        var written = RpadWriter.Write(parsed);
        var reparsed = RpadParser.TryParse(written);

        Assert.NotNull(reparsed);
        Assert.Equal(kind, reparsed!.FrontMatter.Kind);
        Assert.DoesNotContain('`', reparsed.RunCode);

        // A THIRD round trip (write what was just reparsed, parse it again) stays just as clean —
        // guards against the closing fence silently getting swallowed into RunCode on one pass (the
        // bug this test targets), which would only show up as corruption one save later, not on the
        // first save alone.
        var written2 = RpadWriter.Write(reparsed);
        var reparsed2 = RpadParser.TryParse(written2);
        Assert.NotNull(reparsed2);
        Assert.Equal(kind, reparsed2!.FrontMatter.Kind);
        Assert.DoesNotContain('`', reparsed2.RunCode);
    }

    // NewFile used to hardcode `language: csharp` and the ```csharp fence
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

    // An unparseable value used to reset the *whole* front matter to defaults (C#, no
    // packages), which a later save then persisted. It's now an explicit parse error instead.
    [Fact]
    public void Parse_InvalidKindValue_ReportsErrorInsteadOfDefaultFrontMatter()
    {
        const string content =
            "---\nversion: 1\nlanguage: csharp\ntargetSdk: net10.0\nkind: NotARealKind\npackages:\n  Dapper: 2.1.66\n---\n\n```csharp run\n1.Dump();\n```\n";

        var result = RpadParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Document);
        Assert.Contains("Invalid YAML front matter (line 5)", result.Error);
        Assert.Contains("NotARealKind", result.Error);
        Assert.Null(RpadParser.TryParse(content));
    }

    [Fact]
    public void Parse_MalformedVbMetadataWithPackagesAndReferences_ReportsErrorNotCSharpDefaults()
    {
        const string content =
            "---\nversion: 1.0\nlanguage: vb\ntargetSdk: net10.0\nkind: nonsense\npackages:\n  Newtonsoft.Json: 13.0.3\nreferences:\n  - ../Shared/Shared.vbproj\n---\n\n# VB\n\n```vb run\nConsole.WriteLine(1)\n```\n";

        var result = RpadParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Contains("nonsense", result.Error);
    }

    [Theory]
    [InlineData("Console.WriteLine(1);\n", "does not start with")]
    [InlineData("---\nlanguage: csharp\n\n```csharp run\n1.Dump();\n```\n", "not closed")]
    [InlineData("---\nlanguage: [csharp\n---\n\n```csharp run\n1.Dump();\n```\n", "Invalid YAML front matter")]
    [InlineData("---\npackages:\n  - Dapper\n---\n\n```csharp run\n1.Dump();\n```\n", "Invalid YAML front matter")]
    // Declared VB, but only a C# block — never guess at a different language's block.
    [InlineData("---\nlanguage: vb\n---\n\n```csharp run\n1.Dump();\n```\n", "No '```vb run' code block")]
    [InlineData("---\nlanguage: csharp\n---\n\n# Notes only\n", "No '```csharp run' code block")]
    public void Parse_InvalidDocument_ReportsDiagnostic(string content, string expectedError)
    {
        var result = RpadParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.Error);
    }

    // YamlDotNet's enum binding accepted numbers and the placeholder member,
    // so `kind: 99` parsed as an undefined RpadKind and `kind: Unknown`/`0` silently became Statements.
    [Theory]
    [InlineData("kind: 99", "Unsupported kind '99'")]
    [InlineData("kind: 0", "Unsupported kind '0'")]
    [InlineData("kind: 1", "Unsupported kind '1'")]
    [InlineData("kind: '2'", "Unsupported kind '2'")]
    [InlineData("kind: Unknown", "Unsupported kind 'Unknown'")]
    [InlineData("kind: unknown", "Unsupported kind 'unknown'")]
    [InlineData("kind: -1", "Unsupported kind '-1'")]
    [InlineData("language: Unknown", "Unsupported language 'Unknown'")]
    [InlineData("language: 0", "Unsupported language '0'")]
    [InlineData("language: 2", "Unsupported language '2'")]
    [InlineData("language: python", "Unsupported language 'python'")]
    [InlineData("kind: [Program]", "Invalid YAML front matter")]
    public void Parse_UnsupportedEnumValue_ReportsErrorWithLine(string line, string expectedError)
    {
        var content = $"---\nversion: 1.0\n{line}\n---\n\n```csharp run\n1.Dump();\n```\n";

        var result = RpadParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.Error);
        Assert.Contains("(line 3)", result.Error);
    }

    [Theory]
    [InlineData("kind:", RpadKind.Statements)]
    [InlineData("kind: ~", RpadKind.Statements)]
    [InlineData("kind: null", RpadKind.Statements)]
    [InlineData("kind: ''", RpadKind.Statements)]
    [InlineData("kind: Program", RpadKind.Program)]
    [InlineData("kind: expression", RpadKind.Expression)]
    public void Parse_EmptyOrDeclaredKind_IsAccepted(string line, RpadKind expected)
    {
        var result = RpadParser.Parse($"---\n{line}\n---\n\n```csharp run\n1.Dump();\n```\n");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Document.FrontMatter.Kind);
    }

    [Theory]
    [InlineData("language: ~", RpadLanguage.CSharp, "csharp")]
    [InlineData("language: csharp", RpadLanguage.CSharp, "csharp")]
    [InlineData("language: VB", RpadLanguage.Vb, "vb")]
    public void Parse_EmptyOrDeclaredLanguage_IsAccepted(string line, RpadLanguage expected, string fence)
    {
        var result = RpadParser.Parse($"---\n{line}\n---\n\n```{fence} run\n1\n```\n");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Document.FrontMatter.Language);
    }

    // ── Delimiter lines ─────────────────────────────

    [Theory]
    [InlineData("---x\nlanguage: csharp\n---\n\n```csharp run\n1\n```\n", "does not start")]
    [InlineData("----\nlanguage: csharp\n---\n\n```csharp run\n1\n```\n", "does not start")]
    [InlineData("---\nlanguage: csharp\n---garbage\n\n```csharp run\n1\n```\n", "not closed")]
    [InlineData("---\nlanguage: csharp\n----\n\n```csharp run\n1\n```\n", "not closed")]
    [InlineData("---\nlanguage: csharp\n --- \n\n```csharp run\n1\n```\n", "not closed")]
    [InlineData("---\n---\n\n```csharp runner\n1\n```\n", "No '```csharp run' code block")]
    [InlineData("---\n---\n\n```csharprun\n1\n```\n", "No '```csharp run' code block")]
    [InlineData("---\n---\n\n```csharp run extra\n1\n```\n", "No '```csharp run' code block")]
    [InlineData("---\n---\n\n```csharp\n1\n```\n", "No '```csharp run' code block")]
    [InlineData("---\n---\n\n````csharp run\n1\n````\n", "No '```csharp run' code block")]
    [InlineData("---\n---\n\n ```csharp run\n1\n```\n", "No '```csharp run' code block")]
    public void Parse_MalformedDelimiterLine_ReportsErrorInsteadOfGuessing(string content, string expectedError)
    {
        var result = RpadParser.Parse(content);

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.Error);
        Assert.Null(RpadParser.TryParse(content));
    }

    [Theory]
    [InlineData("--- \t\nkind: Program\n---  \n\n```csharp run \t\n1.Dump();\n```  \nAFTER\n")]
    [InlineData("---\r\nkind: Program\r\n--- \r\n\r\n```csharp run \r\n1.Dump();\r\n``` \r\nAFTER\r\n")]
    [InlineData("---\nkind: Program\n---\n\n```CSharp   RUN\n1.Dump();\n```\nAFTER\n")]
    [InlineData("---\nkind: Program\n---\n\n```csharp\trun\n1.Dump();\n```\nAFTER\n")]
    public void Parse_DelimitersWithTrailingWhitespace_AreAccepted(string content)
    {
        var result = RpadParser.Parse(content);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(RpadKind.Program, result.Document.FrontMatter.Kind);
        Assert.StartsWith("1.Dump();", result.Document.RunCode);
        Assert.DoesNotContain("`", result.Document.RunCode);
        Assert.EndsWith("AFTER" + (content.Contains('\r') ? "\r\n" : "\n"), RpadWriter.Write(result.Document));
    }

    // A near-miss run line earlier in the Markdown stays Markdown; the real run block is used.
    [Fact]
    public void Parse_NearMissRunLineBeforeTheRealBlock_IsKeptAsMarkdown()
    {
        const string content = CanonicalFm + "\n```csharp runner\nnot this\n```\n\n```csharp run\n1.Dump();\n```\n";

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal("1.Dump();\n", parsed!.RunCode);
        Assert.Equal(content, RpadWriter.Write(parsed));
    }

    [Theory]
    [InlineData("``` x\nmore();\n```\n", "a();\n``` x\nmore();\n")]   // "``` x" is not a closing fence
    [InlineData("````\nAFTER\n", "a();\n")]                         // longer backtick run closes
    [InlineData("```   \nAFTER\n", "a();\n")]                        // trailing whitespace
    public void Parse_ClosingFence_RequiresBackticksOnly(string tail, string expectedCode)
    {
        var content = CanonicalFm + "```csharp run\na();\n" + tail;

        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(expectedCode, parsed!.RunCode);
        Assert.Equal(content, RpadWriter.Write(parsed));
    }

    [Theory]
    [InlineData("---\n---\n\n```csharp run\n1.Dump();\n```\n")]
    [InlineData("---\r\n---\r\n\r\n```csharp run\r\n1.Dump();\r\n```\r\n")]
    [InlineData("---\n# only a comment\n---\n\n```csharp run\n1.Dump();\n```\n")]
    public void Parse_EmptyFrontMatter_UsesDefaults(string content)
    {
        var result = RpadParser.Parse(content);

        Assert.True(result.IsSuccess, result.Error);
        var fm = result.Document.FrontMatter;
        Assert.Equal("1.0", fm.Version);
        Assert.Equal(RpadLanguage.CSharp, fm.Language);
        Assert.Equal(RpadKind.Statements, fm.Kind);
        Assert.Equal(TargetSdk.Default, fm.TargetSdk);
        Assert.Empty(fm.Packages);
        Assert.StartsWith("1.Dump();", result.Document.RunCode);
    }

    [Fact]
    public void Parse_NullFields_UseDefaults()
    {
        const string content =
            "---\nversion:\nlanguage:\ntargetSdk:\nkind:\npackages:\nreferences:\nnamespaces:\n---\n\n```csharp run\n1.Dump();\n```\n";

        var result = RpadParser.Parse(content);

        Assert.True(result.IsSuccess, result.Error);
        var fm = result.Document.FrontMatter;
        Assert.Equal("1.0", fm.Version);
        Assert.Equal(RpadLanguage.CSharp, fm.Language);
        Assert.Equal(RpadKind.Statements, fm.Kind);
        Assert.Equal(TargetSdk.Default, fm.TargetSdk);
        Assert.Empty(fm.Packages);
        Assert.Empty(fm.References!);
        Assert.Empty(fm.Namespaces!);
    }

    // ── Empty code block / fence detection ──────────────────────

    // Same front matter RpadWriter emits, so Write(Parse(x)) == x is a meaningful byte-for-byte check.
    private const string CanonicalFm = "---\nversion: 1.0\nlanguage: csharp\ntargetSdk: latest\nkind: Statements\n---\n";

    [Theory]
    [InlineData(CanonicalFm + "```csharp run\n```\nAFTER\n")]
    [InlineData(CanonicalFm + "\n# Title\n\nBefore.\n\n```csharp run\n```\n\nAfter, with `inline` code.\n\n```text\nnot code\n```\n")]
    [InlineData(CanonicalFm + "\n# Title\n\n```csharp run\n```")]                     // closing fence at EOF
    [InlineData(CanonicalFm + "\r\n# Title\r\n\r\n```csharp run\r\n```\r\nAFTER\r\n")] // CRLF body
    public void Parse_EmptyBlockClosedImmediately_HasEmptyCodeAndRoundTripsExactly(string content)
    {
        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.RunCode);
        Assert.Equal(content, RpadWriter.Write(parsed));
    }

    [Theory]
    [InlineData(CanonicalFm + "\n# Title\n\n```csharp run\nvar x = 1;\n\nx.Dump();\n```\n\nAfter.\n", "var x = 1;\n\nx.Dump();\n")]
    [InlineData(CanonicalFm + "\r\n# Title\r\n\r\n```csharp run\r\nvar x = 1;\r\nx.Dump();\r\n```\r\nAfter.\r\n", "var x = 1;\r\nx.Dump();\r\n")]
    [InlineData(CanonicalFm + "```csharp run\n1.Dump();\n```", "1.Dump();\n")]
    public void Parse_CodeBlockWithSurroundingMarkdown_RoundTripsExactly(string content, string expectedCode)
    {
        var parsed = RpadParser.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(expectedCode, parsed!.RunCode);
        Assert.Equal(content, RpadWriter.Write(parsed));
    }

    [Fact]
    public void Write_EmptyRunCode_DoesNotIntroduceABlankCodeLine()
    {
        const string content = CanonicalFm + "\n```csharp run\nold code\n```\nAFTER\n";
        var parsed = RpadParser.TryParse(content)!;

        parsed.RunCode = "";
        var written = RpadWriter.Write(parsed);

        Assert.Equal(CanonicalFm + "\n```csharp run\n```\nAFTER\n", written);
        Assert.Equal("", RpadParser.TryParse(written)!.RunCode);
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
