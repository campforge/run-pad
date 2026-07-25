using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #50: completion/signature-help/quick-info descriptions used to be a single plain-text
// signature string with no XML-doc summary and no syntax highlighting. Fixed by carrying a
// classified DisplayTextSegment signature plus a separately-parsed <summary>/<param> string — these
// tests exercise that through the real CSharpIntelliSenseService/Roslyn pipeline, not just the
// isolated XmlDocSummary/SignatureFormatting helpers, since XML-doc-comment retrieval only works
// against a real compiled symbol.
public class CSharpIntelliSenseRichTooltipTests
{
    private const string DocumentedGreeter =
        """
        public class Greeter
        {
            /// <summary>Builds a greeting for <paramref name="name"/>, similar to <see cref="Greeter"/>.</summary>
            /// <param name="name">The person to greet.</param>
            /// <param name="times">How many times to repeat it.</param>
            public string Greet(string name, int times) => name;
        }

        var g = new Greeter();

        """;

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task Completions_Should_ExposeParsedSummary_AndHighlightedSignature()
    {
        using var service = CreateService();
        var code = DocumentedGreeter + "g.Greet";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        var item = Assert.Single(result!.Items, i => i.DisplayText == "Greet");
        Assert.Equal("Builds a greeting for name, similar to Greeter.", item.Summary);
        Assert.Contains(item.Signature, s => s.Kind == SemanticTokenKind.MethodName && s.Text == "Greet");
        Assert.Contains(item.Signature, s => s.Kind == SemanticTokenKind.Keyword && s.Text == "string");
    }

    [Fact]
    public async Task Completions_ForAKeyword_Should_CarryAKeywordSegment_AndNoSummary()
    {
        using var service = CreateService();

        var result = await service.GetCompletionsAsync(string.Empty, 0, ct: TestContext.Current.CancellationToken);

        var item = Assert.Single(result!.Items, i => i.DisplayText == "class");
        var segment = Assert.Single(item.Signature);
        Assert.Equal(SemanticTokenKind.Keyword, segment.Kind);
        Assert.Null(item.Summary);
    }

    [Fact]
    public async Task QuickInfo_Should_ExposeParsedSummary_AndHighlightedSignature()
    {
        using var service = CreateService();
        var code = DocumentedGreeter + "g.Greet(\"a\", 1);";
        var position = code.IndexOf("Greet(\"a\"", StringComparison.Ordinal) + 1;

        var result = await service.GetQuickInfoAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Builds a greeting for name, similar to Greeter.", result!.Summary);
        Assert.Contains(result.Signature, s => s.Kind == SemanticTokenKind.MethodName && s.Text == "Greet");
    }

    [Fact]
    public async Task SignatureHelp_Should_EmphasizeActiveParameter_AndExposePerParameterSummary()
    {
        using var service = CreateService();
        var code = DocumentedGreeter + "g.Greet(\"a\", ";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var item = Assert.Single(result!.Items);
        Assert.Equal(1, result.ArgumentIndex);
        Assert.Equal("Builds a greeting for name, similar to Greeter.", item.Summary);
        Assert.Equal("The person to greet.", item.Parameters[0].Summary);
        Assert.Equal("How many times to repeat it.", item.Parameters[1].Summary);

        var timesSegment = Assert.Single(item.Signature, s => s.Text == "times");
        Assert.True(timesSegment.Emphasis);
        Assert.DoesNotContain(item.Signature, s => s.Text == "name" && s.Emphasis);
    }

    // Regression guard for the actual bug report: the BCL assemblies CSharpReferenceService.GetDefault()
    // compiles against have no sibling .xml (they're the runtime's implementation DLLs, not the
    // documented reference-assembly copies — see ReferenceDocumentation), so without that lookup this
    // came back null for every BCL symbol even though the signature was still highlighted correctly.
    [Fact]
    public async Task Completions_ForABclMember_Should_AlsoExposeSummary()
    {
        using var service = CreateService();
        var code = $"var sb = new {typeof(StringBuilder).FullName}();\nsb.Append";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        var item = Assert.Single(result!.Items, i => i.DisplayText == "Append");
        Assert.False(string.IsNullOrWhiteSpace(item.Summary));
    }

    // The flagship scenario: RunPad.Runtime.csproj now sets GenerateDocumentationFile, so the
    // <summary> already written on ObjectExtensions.Dump() (see ObjectExtensions.cs) should actually
    // reach the completion tooltip instead of being stuck in a .xml file nobody points Roslyn at.
    // Which of the 5 Dump() overloads wins the completion list's per-name dedup isn't a stable
    // contract, so this only asserts that *a* real summary comes through, not its exact wording.
    [Fact]
    public async Task Completions_ForDump_Should_ExposeItsRealXmlDocSummary()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        var item = Assert.Single(result!.Items, i => i.DisplayText == "Dump");
        Assert.False(string.IsNullOrWhiteSpace(item.Summary));
        Assert.Contains("Dump", item.Summary);
    }
}
