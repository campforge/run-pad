using Microsoft.CodeAnalysis.CSharp;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using Xunit;

namespace RunPad.NonUI.Tests;

public class ProgramBodyProjectionTests
{
    private static readonly CSharpParseOptions Options = CSharpScriptCompiler.ParseOptions(10, 0);
    private const string Code = "void Main() { 2.Twice(); }\r\nstatic class Extensions { public static int Twice(this int n) => n * 2; }\r\nvoid Later() { 3.Twice(); }";

    [Fact]
    public void OriginalCharactersAndLines_Should_RoundTripThroughProjection()
    {
        var projection = ProgramBodyProjection.Create(Code, Options);
        Assert.True(projection.HasChanges);
        for (var offset = 0; offset <= Code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
        Assert.Equal(Code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));
        Assert.Equal(Code.Count(c => c == '\r'), projection.Text.Count(c => c == '\r'));
        Assert.Equal(2, RpadKindTransform.ProgramLineOffset(Code, Options));
    }

    [Fact]
    public void SourceEdit_Should_RemoveProjectionWithoutChangingOtherSource()
    {
        var projection = ProgramBodyProjection.Create(Code, Options);
        var changed = projection.Text.Replace("n * 2", "n * 3", StringComparison.Ordinal);
        var restored = projection.RestoreEdit(changed, [], RpadKindTransform.ProgramPrefixFor(Code, Options),
            RpadKindTransform.ProgramSuffixFor(Code, Options), Options);
        Assert.NotNull(restored);
        Assert.Equal(Code.Replace("n * 2", "n * 3", StringComparison.Ordinal), restored.Value.Code);
        var span = Assert.Single(restored.Value.ChangedSpans);
        Assert.Equal("3", restored.Value.Code.Substring(span.Start, span.Length));
    }

    [Fact]
    public void EditsToGeneratedClassBoundaries_Should_BeRefused()
    {
        var projection = ProgramBodyProjection.Create(Code, Options);
        var changed = projection.Text.Replace("partial class __RunPadProgram", "partial class Changed", StringComparison.Ordinal);
        Assert.Null(projection.RestoreEdit(changed, [], RpadKindTransform.ProgramPrefixFor(Code, Options),
            RpadKindTransform.ProgramSuffixFor(Code, Options), Options));
    }

    [Fact]
    public void OnlyStaticClassesAndNamespaces_Should_LeaveTheGeneratedClass()
    {
        const string code = "void Main() {}\nclass Value {}\nstatic class Helpers {}\nstatic class Extensions { public static int Twice(this int n) => n*2; }\nnamespace Tools { class Inner {} }\nenum Kind { A }\n";
        var text = ProgramBodyProjection.Create(code, Options).Text;
        Assert.Contains("\nclass Value {}\n", text, StringComparison.Ordinal);
        Assert.Contains("\nenum Kind { A }\n", text, StringComparison.Ordinal);
        Assert.Contains("}static class Helpers {}partial class", text, StringComparison.Ordinal);
        Assert.Contains("}static class Extensions", text, StringComparison.Ordinal);
        Assert.Contains("}namespace Tools", text, StringComparison.Ordinal);
        Assert.StartsWith("using static " + RpadKindTransform.ProgramWrapperClassName + ";",
            RpadKindTransform.ProgramPrefixFor(code, Options), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalProgramsAndNestedExtensionDeclarations_Should_NotBeRewritten()
    {
        const string nested = "void Main() {}\nclass Container { static class Extensions { static int Twice(this int n) => n*2; } }";
        Assert.False(ProgramBodyProjection.Create(nested, Options).HasChanges);
        const string classic = "class Program { static void Main() {} } static class Extensions { public static int Twice(this int n) => n*2; }";
        Assert.False(ProgramBodyProjection.Create(classic, Options).HasChanges);
    }

    [Fact]
    public void LeadingUsingDirectives_Should_MoveBeforeTheWrapperClassAndBeCompilable()
    {
        const string code = "using System;\nusing System.Linq;\n\nvoid Main() { new[] { 1, 2 }.Sum().Dump(); }\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.True(projection.HasChanges);
        Assert.Equal("using System;\nusing System.Linq;", projection.LeadingDirectives);
        Assert.DoesNotContain("using System;", projection.Text, StringComparison.Ordinal);
        Assert.Equal(new string(' ', "using System;".Length) + "\n" + new string(' ', "using System.Linq;".Length) + "\n\nvoid Main()",
            projection.Text[..("using System;".Length + 1 + "using System.Linq;".Length + 1 + 1 + "void Main()".Length)]);

        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        Assert.StartsWith("using System;\nusing System.Linq;", compiled, StringComparison.Ordinal);
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        // 2 lines for the call statement's own class header/brace, plus 1 for the embedded '\n'
        // between the two hoisted using directives.
        Assert.Equal(3, RpadKindTransform.ProgramLineOffset(code, Options));

        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }

    [Fact]
    public void LeadingExternAliasAndUsing_Should_BothMoveAndRoundTrip()
    {
        const string code = "extern alias Json;\nusing System;\nvoid Main() {}\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.Equal("extern alias Json;\nusing System;", projection.LeadingDirectives);
        Assert.DoesNotContain("extern alias", projection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("using System;", projection.Text, StringComparison.Ordinal);
        Assert.Equal(code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));
        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }

    [Fact]
    public void SwitchingFromStatementsToProgram_Should_ProduceACompilableTemplateWithLeadingUsings()
    {
        // What ProgramTemplate.CodeForKind generates for a Statements script that starts with usings,
        // after the editor's snippet caret (${0}) is accepted and left empty.
        const string code = "using System;\n\nvoid Main()\n{\n    \n}\n\n// Other methods, fields and types used by Main go here.\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void NoLeadingDirectives_Should_LeaveLineOffsetAtTwo()
    {
        const string code = "void Main() {}\n";
        Assert.Equal("", ProgramBodyProjection.Create(code, Options).LeadingDirectives);
        Assert.Equal(2, RpadKindTransform.ProgramLineOffset(code, Options));
    }

    [Fact]
    public void LeadingUsingInsideActiveIfDirective_Should_MoveTheWholeBlockAndBeCompilable()
    {
        // DEBUG is defined by Options (see CSharpScriptCompiler.ParseOptions), so the #if is taken:
        // "using System.Text;" is a real using directive, nested between the #if/#endif pair whose
        // naive bounds (directives[0].SpanStart..directives[^1].Span.End) stop right after it and
        // leave the #endif behind in the body.
        const string code = "using System;\n#if DEBUG\nusing System.Text;\n#endif\nvoid Main() { new StringBuilder().Append(1).Dump(); }\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.True(projection.HasChanges);
        Assert.Equal("using System;\n#if DEBUG\nusing System.Text;\n#endif", projection.LeadingDirectives);
        Assert.DoesNotContain("#if", projection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#endif", projection.Text, StringComparison.Ordinal);
        Assert.Equal(code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));

        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        // 2 lines for the class header/brace, 3 for the embedded '\n's inside the hoisted block, plus
        // 1 more because the block ends in '#endif' and so can't share the call statement's line.
        Assert.Equal(6, RpadKindTransform.ProgramLineOffset(code, Options));

        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }

    [Fact]
    public void LeadingUsingInsideIfElseDirective_Should_MoveTheWholeBlockAndBeCompilable()
    {
        // DEBUG is defined, so the #if branch is taken and the #else branch is disabled text; both
        // still have to move as a single block, including the #else itself.
        const string code = "using System;\n#if DEBUG\nusing System.Text;\n#else\nusing System.Linq;\n#endif\nvoid Main() { new StringBuilder().Append(1).Dump(); }\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.Equal("using System;\n#if DEBUG\nusing System.Text;\n#else\nusing System.Linq;\n#endif", projection.LeadingDirectives);
        Assert.DoesNotContain('#', projection.Text);
        Assert.Equal(code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));

        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        // 2 for the class header/brace, 5 for the embedded '\n's inside the hoisted block, plus 1
        // more because the block ends in '#endif' and so can't share the call statement's line.
        Assert.Equal(8, RpadKindTransform.ProgramLineOffset(code, Options));

        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }

    [Fact]
    public void LeadingUsingInsideIfDirectiveWithTrailingComment_Should_StillGetItsOwnLine()
    {
        // "#endif // imports" is a valid directive line: the comment is part of the #endif's own
        // trivia, so a text match for the literal "#endif" at the end of LeadingDirectives would miss
        // it and glue the call statement onto the same line, merging it into the comment (CS1024).
        const string code = "using System;\n#if DEBUG\nusing System.Text;\n#endif // imports\nvoid Main() { new StringBuilder().Append(1).Dump(); }\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.True(projection.LeadingDirectivesEndWithConditionalBlock);
        Assert.Equal("using System;\n#if DEBUG\nusing System.Text;\n#endif // imports", projection.LeadingDirectives);
        Assert.DoesNotContain('#', projection.Text);
        Assert.Equal(code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));

        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        Assert.DoesNotContain("// imports\nvoid", compiled, StringComparison.Ordinal);
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        // 2 for the class header/brace, 3 for the embedded '\n's inside the hoisted block, plus 1
        // more because the block ends in '#endif // imports' and so can't share the call statement's line.
        Assert.Equal(6, RpadKindTransform.ProgramLineOffset(code, Options));

        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }

    [Fact]
    public void LeadingUsingInsideIfDirectiveWithTrailingSpaces_Should_StillGetItsOwnLine()
    {
        // "#endif   " (trailing spaces, no comment) is likewise a valid directive line that a plain
        // text match for "#endif" at the very end of LeadingDirectives would miss.
        const string code = "using System;\n#if DEBUG\nusing System.Text;\n#endif   \nvoid Main() { new StringBuilder().Append(1).Dump(); }\n";
        var projection = ProgramBodyProjection.Create(code, Options);
        Assert.True(projection.LeadingDirectivesEndWithConditionalBlock);
        Assert.Equal("using System;\n#if DEBUG\nusing System.Text;\n#endif   ", projection.LeadingDirectives);
        Assert.DoesNotContain('#', projection.Text);
        Assert.Equal(code.Count(c => c == '\n'), projection.Text.Count(c => c == '\n'));

        var prefix = RpadKindTransform.ProgramPrefixFor(code, Options);
        var suffix = RpadKindTransform.ProgramSuffixFor(code, Options);
        var compiled = prefix + projection.Text + suffix;
        var tree = CSharpSyntaxTree.ParseText(compiled, Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        Assert.Equal(6, RpadKindTransform.ProgramLineOffset(code, Options));

        for (var offset = 0; offset <= code.Length; offset++)
            Assert.Equal(offset, projection.ToOriginal(projection.ToProjected(offset)));
    }
}
