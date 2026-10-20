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
}
