using Xunit;

namespace RunPad.Format.RPad.Tests;

// LinqConverter.Convert. Fixtures below are representative
// hand-written .linq content matching LINQPad's documented file shape (XML <Query> header + plain
// trailing source, no XML wrapping on the code itself), not drawn from a real corpus of files (none
// available in this environment) — the format itself is what's under test, not any specific sample.
public class LinqConverterTests
{
    [Fact]
    public void Convert_Statements_MapsToCSharpStatements()
    {
        const string linq = """
            <Query Kind="Statements" />

            Console.WriteLine("hi");
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Equal(RpadLanguage.CSharp, doc.FrontMatter.Language);
        Assert.Equal(RpadKind.Statements, doc.FrontMatter.Kind);
        // No trailing '\n' here — this raw string literal's last content line has none either (its
        // closing """ sits on the very next line); RpadWriter.Write is what guarantees one on the way
        // out, not the converter (see the round-trip test below).
        Assert.Equal("Console.WriteLine(\"hi\");", doc.RunCode);
    }

    [Theory]
    [InlineData("Program", RpadLanguage.CSharp, RpadKind.Program)]
    [InlineData("Expression", RpadLanguage.CSharp, RpadKind.Expression)]
    [InlineData("VBStatements", RpadLanguage.Vb, RpadKind.Statements)]
    [InlineData("VBProgram", RpadLanguage.Vb, RpadKind.Program)]
    [InlineData("VBExpression", RpadLanguage.Vb, RpadKind.Expression)]
    public void Convert_Should_MapEveryRunPadSupportedKind(string linqKind, RpadLanguage expectedLanguage, RpadKind expectedKind)
    {
        var linq = $"""
            <Query Kind="{linqKind}" />

            code
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Equal(expectedLanguage, doc.FrontMatter.Language);
        Assert.Equal(expectedKind, doc.FrontMatter.Kind);
    }

    [Fact]
    public void Convert_Should_ExtractNuGetReferences_WithAndWithoutAnExplicitVersion()
    {
        const string linq = """
            <Query Kind="Statements">
              <NuGetReference>Newtonsoft.Json</NuGetReference>
              <NuGetReference Version="13.0.3">System.Text.Json</NuGetReference>
            </Query>

            code
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Equal(Package.Default, doc.FrontMatter.Packages["Newtonsoft.Json"]);
        Assert.Equal(new Package("13.0.3"), doc.FrontMatter.Packages["System.Text.Json"]);
    }

    [Fact]
    public void Convert_Should_ExtractNamespaces()
    {
        const string linq = """
            <Query Kind="Statements">
              <Namespace>System.Text</Namespace>
              <Namespace>System.Text.Json</Namespace>
            </Query>

            code
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Equal(["System.Text", "System.Text.Json"], doc.FrontMatter.Namespaces);
    }

    [Fact]
    public void Convert_Should_ExtractReferences()
    {
        const string linq = """
            <Query Kind="Statements">
              <Reference>System.Data.dll</Reference>
              <Reference Relative="..\lib\MyLib.dll">..\lib\MyLib.dll</Reference>
            </Query>

            code
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Equal(["System.Data.dll", @"..\lib\MyLib.dll"], doc.FrontMatter.References);
    }

    // The header is only the FIRST part of the file that's actually XML — the trailing code usually
    // isn't (unescaped '<'/'&', etc.) — so the converter must isolate just the <Query>...</Query>
    // fragment rather than treating the whole file as one XML document.
    [Fact]
    public void Convert_Should_NotChokeOnCodeContainingXmlSpecialCharacters()
    {
        const string linq = """
            <Query Kind="Statements" />

            if (1 < 2 && true)
            {
                Console.WriteLine("a & b");
            }
            """;

        var doc = LinqConverter.Convert(linq);

        Assert.Contains("1 < 2 && true", doc.RunCode);
        Assert.Contains("a & b", doc.RunCode);
    }

    [Fact]
    public void Convert_Should_TrimBlankLinesBetweenTheHeaderAndTheCode()
    {
        const string linq = "<Query Kind=\"Statements\" />\n\n\n\nConsole.WriteLine(1);\n";

        var doc = LinqConverter.Convert(linq);

        Assert.Equal("Console.WriteLine(1);\n", doc.RunCode);
    }

    [Fact]
    public void Convert_Should_ProduceADocument_WritableByRpadWriter_AndReparsableByRpadParser()
    {
        const string linq = """
            <Query Kind="Statements">
              <NuGetReference>Newtonsoft.Json</NuGetReference>
              <Namespace>System.Text</Namespace>
            </Query>

            Console.WriteLine("hi");
            """;

        var doc = LinqConverter.Convert(linq);
        var rpadText = RpadWriter.Write(doc);
        var reparsed = RpadParser.TryParse(rpadText);

        Assert.NotNull(reparsed);
        Assert.Equal(RpadKind.Statements, reparsed!.FrontMatter.Kind);
        Assert.Equal(RpadLanguage.CSharp, reparsed.FrontMatter.Language);
        Assert.Contains("Newtonsoft.Json", reparsed.FrontMatter.Packages.Keys);
        Assert.Contains("System.Text", reparsed.FrontMatter.Namespaces!);
        Assert.Equal("Console.WriteLine(\"hi\");\n", reparsed.RunCode);
    }

    [Fact]
    public void Convert_Should_Throw_WhenThereIsNoQueryHeaderAtAll()
    {
        const string notLinq = "Console.WriteLine(1);\n";

        Assert.Throws<FormatException>(() => LinqConverter.Convert(notLinq));
    }

    [Fact]
    public void Convert_Should_Throw_WhenTheQueryHeaderIsNeverClosed()
    {
        const string malformed = "<Query Kind=\"Statements\"\nConsole.WriteLine(1);\n";

        Assert.Throws<FormatException>(() => LinqConverter.Convert(malformed));
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("ESQL")]
    [InlineData("FSharpProgram")]
    [InlineData("FSharpExpression")]
    public void Convert_Should_Throw_ForKindsRunPadDoesNotSupport(string unsupportedKind)
    {
        var linq = $"<Query Kind=\"{unsupportedKind}\" />\n\ncode\n";

        var ex = Assert.Throws<FormatException>(() => LinqConverter.Convert(linq));
        Assert.Contains(unsupportedKind, ex.Message);
    }

    [Fact]
    public void Convert_Should_Throw_WhenTheQueryElementHasNoKindAttribute()
    {
        const string linq = "<Query />\n\ncode\n";

        Assert.Throws<FormatException>(() => LinqConverter.Convert(linq));
    }
}
