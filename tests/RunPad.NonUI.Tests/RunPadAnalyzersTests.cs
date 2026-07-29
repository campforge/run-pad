using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense.Analyzers;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 (сто сорок второй проход) — direct, real-Roslyn-compilation tests against the two new
// hand-written analyzers (EmptyCatchClauseAnalyzer/DuplicateEnumValueAnalyzer) in isolation, one level
// below CSharpIntelliSenseAnalyzersTests.cs's full-service integration test — faster to write many
// shape variations against, since it skips AdhocWorkspace/preamble-offset machinery entirely.
public class RunPadAnalyzersTests
{
    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(DiagnosticAnalyzer analyzer, string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var references = new CSharpReferenceService().GetDefault()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers([analyzer]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    // ── EmptyCatchClauseAnalyzer (RPAD0001) ─────────────────────────────────────

    [Theory]
    [InlineData("try { M(); } catch { }")]
    [InlineData("try { M(); } catch (System.Exception) { }")]
    [InlineData("try { M(); } catch (System.Exception ex) { }")]
    public async Task EmptyCatchClauseAnalyzer_Should_Flag_AnEmptyCatchThatSwallowsSystemException(string catchStatement)
    {
        var code = $"class C {{ void M() {{ }} void N() {{ {catchStatement} }} }}";

        var diagnostics = await GetDiagnosticsAsync(new EmptyCatchClauseAnalyzer(), code);

        Assert.Contains(diagnostics, d => d.Id == EmptyCatchClauseAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task EmptyCatchClauseAnalyzer_Should_NotFlag_ACatchThatActuallyDoesSomething()
    {
        var code = "class C { void M() { } void N() { try { M(); } catch (System.Exception ex) { System.Console.WriteLine(ex); } } }";

        var diagnostics = await GetDiagnosticsAsync(new EmptyCatchClauseAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == EmptyCatchClauseAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task EmptyCatchClauseAnalyzer_Should_NotFlag_AnEmptyCatchOfAMoreSpecificExceptionType()
    {
        // Catching a specific, narrow exception type and ignoring it is a much more deliberate choice
        // than blanket-catching System.Exception — not the pattern this analyzer targets.
        var code = "class C { void M() { } void N() { try { M(); } catch (System.IO.IOException) { } } }";

        var diagnostics = await GetDiagnosticsAsync(new EmptyCatchClauseAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == EmptyCatchClauseAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task EmptyCatchClauseAnalyzer_Should_NotFlag_AnEmptyCatchWithAWhenFilter()
    {
        var code = "class C { void M() { } void N() { try { M(); } catch (System.Exception) when (false) { } } }";

        var diagnostics = await GetDiagnosticsAsync(new EmptyCatchClauseAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == EmptyCatchClauseAnalyzer.DiagnosticId);
    }

    // ── DuplicateEnumValueAnalyzer (RPAD0002) ───────────────────────────────────

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_Flag_TwoMembersWithTheSameExplicitValue()
    {
        var code = "enum E { A = 1, B = 1 }";

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        var d = Assert.Single(diagnostics, d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId);
        Assert.Contains("B", d.GetMessage());
        Assert.Contains("A", d.GetMessage());
    }

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_Flag_EveryLaterDuplicate_NotJustTheSecond()
    {
        var code = "enum E { A = 1, B = 1, C = 1 }";

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        Assert.Equal(2, diagnostics.Count(d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_NotFlag_DistinctExplicitValues()
    {
        var code = "enum E { A = 1, B = 2 }";

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_NotFlag_ImplicitlyNumberedMembers()
    {
        var code = "enum E { A, B, C }"; // 0, 1, 2 — all distinct

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_NotFlag_ANamedBitwiseCombinationThatCollidesWithAnotherMember()
    {
        // AliasOfA's value (1 | 0 == 1) collides with A's, but it's declared AS a bitwise combination
        // of other members, not a copy-pasted literal — the standard [Flags] "named alias" idiom.
        var code = "[System.Flags] enum E { None = 0, A = 1, AliasOfA = A | None }";

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task DuplicateEnumValueAnalyzer_Should_StillFlag_ARealDuplicate_EvenWhenAnExemptCombinationIsAlsoPresent()
    {
        var code = "[System.Flags] enum E { None = 0, A = 1, AliasOfA = A | None, Oops = 1 }";

        var diagnostics = await GetDiagnosticsAsync(new DuplicateEnumValueAnalyzer(), code);

        var d = Assert.Single(diagnostics, d => d.Id == DuplicateEnumValueAnalyzer.DiagnosticId);
        Assert.Contains("Oops", d.GetMessage());
    }

    // ── NotImplementedExceptionAnalyzer (RPAD0003) ──────────────────────────────

    [Fact]
    public async Task NotImplementedExceptionAnalyzer_Should_Flag_AThrowStatement()
    {
        var code = "class C { void M() { throw new System.NotImplementedException(); } }";

        var diagnostics = await GetDiagnosticsAsync(new NotImplementedExceptionAnalyzer(), code);

        Assert.Contains(diagnostics, d => d.Id == NotImplementedExceptionAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task NotImplementedExceptionAnalyzer_Should_Flag_AThrowExpression()
    {
        var code = "class C { int P => throw new System.NotImplementedException(); }";

        var diagnostics = await GetDiagnosticsAsync(new NotImplementedExceptionAnalyzer(), code);

        Assert.Contains(diagnostics, d => d.Id == NotImplementedExceptionAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task NotImplementedExceptionAnalyzer_Should_Flag_RegardlessOfConstructorArguments()
    {
        var code = "class C { void M() { throw new System.NotImplementedException(\"todo\"); } }";

        var diagnostics = await GetDiagnosticsAsync(new NotImplementedExceptionAnalyzer(), code);

        Assert.Contains(diagnostics, d => d.Id == NotImplementedExceptionAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task NotImplementedExceptionAnalyzer_Should_NotFlag_ADifferentExceptionType()
    {
        var code = "class C { void M() { throw new System.InvalidOperationException(); } }";

        var diagnostics = await GetDiagnosticsAsync(new NotImplementedExceptionAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == NotImplementedExceptionAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task NotImplementedExceptionAnalyzer_Should_NotFlag_AMethodWithARealImplementation()
    {
        var code = "class C { int M() { return 1; } }";

        var diagnostics = await GetDiagnosticsAsync(new NotImplementedExceptionAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == NotImplementedExceptionAnalyzer.DiagnosticId);
    }

    // ── InvalidArgumentNullCheckAnalyzer (RPAD0004) ─────────────────────────────

    [Theory]
    [InlineData("if (a == null) throw new System.ArgumentNullException(nameof(b));")]
    [InlineData("if (null == a) throw new System.ArgumentNullException(nameof(b));")]
    [InlineData("if (a is null) throw new System.ArgumentNullException(nameof(b));")]
    [InlineData("if (a == null) { throw new System.ArgumentNullException(nameof(b)); }")]
    [InlineData("if (a == null) throw new System.ArgumentNullException(\"b\");")]
    public async Task InvalidArgumentNullCheckAnalyzer_Should_Flag_WhenTheNamedParameterDoesNotMatchTheCheckedOne(
        string guardClause)
    {
        var code = $"class C {{ void M(object a, object b) {{ {guardClause} }} }}";

        var diagnostics = await GetDiagnosticsAsync(new InvalidArgumentNullCheckAnalyzer(), code);

        var d = Assert.Single(diagnostics, d => d.Id == InvalidArgumentNullCheckAnalyzer.DiagnosticId);
        Assert.Contains("'a'", d.GetMessage());
        Assert.Contains("'b'", d.GetMessage());
    }

    [Theory]
    [InlineData("if (a == null) throw new System.ArgumentNullException(nameof(a));")]
    [InlineData("if (a is null) throw new System.ArgumentNullException(nameof(a));")]
    [InlineData("if (a == null) throw new System.ArgumentNullException(\"a\");")]
    public async Task InvalidArgumentNullCheckAnalyzer_Should_NotFlag_WhenTheNamedParameterMatches(string guardClause)
    {
        var code = $"class C {{ void M(object a, object b) {{ {guardClause} }} }}";

        var diagnostics = await GetDiagnosticsAsync(new InvalidArgumentNullCheckAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidArgumentNullCheckAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task InvalidArgumentNullCheckAnalyzer_Should_NotFlag_ADifferentExceptionType()
    {
        var code = "class C { void M(object a, object b) { if (a == null) throw new System.ArgumentException(nameof(b)); } }";

        var diagnostics = await GetDiagnosticsAsync(new InvalidArgumentNullCheckAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidArgumentNullCheckAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task InvalidArgumentNullCheckAnalyzer_Should_NotFlag_AConditionItDoesNotUnderstand()
    {
        // A compound condition isn't a simple "check one identifier" shape — deliberately left alone
        // rather than guessed at.
        var code = "class C { void M(object a, object b) { if (a == null || b == null) throw new System.ArgumentNullException(nameof(b)); } }";

        var diagnostics = await GetDiagnosticsAsync(new InvalidArgumentNullCheckAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidArgumentNullCheckAnalyzer.DiagnosticId);
    }
}
