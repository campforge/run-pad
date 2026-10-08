using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense.Analyzers;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 (pass 142) — direct, real-Roslyn-compilation tests against the two new
// hand-written analyzers (EmptyCatchClauseAnalyzer/DuplicateEnumValueAnalyzer) in isolation, one level
// below CSharpIntelliSenseAnalyzersTests.cs's full-service integration test — faster to write many
// shape variations against, since it skips AdhocWorkspace/preamble-offset machinery entirely.
public class RunPadAnalyzersTests
{
    // Shared across all ~3000 tests: Roslyn reuses already-bound assembly symbols only for the SAME
    // MetadataReference instances, so recreating them per test reloaded and rebound the whole BCL
    // reference set every time (most of this class's run time).
    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(() =>
        [.. new CSharpReferenceService().GetDefault().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))]);

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(DiagnosticAnalyzer analyzer, string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly", [tree], DefaultReferences.Value,
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

    // ── CultureSensitiveStringCompareAnalyzer (RPAD0005) ────────────────────────

    [Theory]
    [InlineData("string.Compare(a, b)")]
    [InlineData("System.String.Compare(a, b)")]
    public async Task CultureSensitiveStringCompareAnalyzer_Should_Flag_TheTwoArgumentOverload(string call)
    {
        var code = $"class C {{ void M(string a, string b) {{ {call}; }} }}";

        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveStringCompareAnalyzer(), code);

        Assert.Contains(diagnostics, d => d.Id == CultureSensitiveStringCompareAnalyzer.DiagnosticId);
    }

    [Theory]
    [InlineData("string.Compare(a, b, System.StringComparison.Ordinal)")]
    [InlineData("string.Compare(a, b, true)")]
    [InlineData("string.Compare(a, b, true, System.Globalization.CultureInfo.InvariantCulture)")]
    public async Task CultureSensitiveStringCompareAnalyzer_Should_NotFlag_OverloadsThatAlreadyChooseExplicitly(string call)
    {
        var code = $"class C {{ void M(string a, string b) {{ {call}; }} }}";

        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveStringCompareAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == CultureSensitiveStringCompareAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task CultureSensitiveStringCompareAnalyzer_Should_NotFlag_TheInstanceCompareToMethod()
    {
        var code = "class C { void M(string a, string b) { a.CompareTo(b); } }";

        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveStringCompareAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == CultureSensitiveStringCompareAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task CultureSensitiveStringCompareAnalyzer_Should_NotFlag_ADifferentTypesStaticCompareMethod()
    {
        var code = "class C { void M(System.DateTime a, System.DateTime b) { System.DateTime.Compare(a, b); } }";

        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveStringCompareAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == CultureSensitiveStringCompareAnalyzer.DiagnosticId);
    }

    // ── UnusedPrivateMemberAnalyzer (RPAD0006) ──────────────────────────────────
    // Every "the real work happens here" method below is deliberately `public void M()`, not the bare
    // `void M()` used elsewhere in this file — a private, uncalled M() would itself be flagged by this
    // very analyzer (correctly!) and contaminate the "should NOT flag X" assertions below.

    [Theory]
    [InlineData("class C { private int _unused; public void M() { } }")]
    [InlineData("class C { int _unused; public void M() { } }")] // no modifier — still private by default
    public async Task UnusedPrivateMemberAnalyzer_Should_Flag_AnUnusedPrivateField(string code)
    {
        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        var d = Assert.Single(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
        Assert.Contains("_unused", d.GetMessage());
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_Flag_AnUnusedPrivateMethod()
    {
        var code = "class C { private void Unused() { } public void M() { } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        var d = Assert.Single(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
        Assert.Contains("Unused", d.GetMessage());
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_AFieldReferencedElsewhereInTheType()
    {
        var code = "class C { private int _count; public void M() { _count++; } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_AMethodCalledElsewhereInTheType()
    {
        var code = "class C { private void Helper() { } public void M() { Helper(); } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_AFieldWithAnAttribute()
    {
        // Conservative: could be reached via reflection/serialization/DI.
        var code = "class C { [System.Obsolete] private int _unused; public void M() { } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_APublicField()
    {
        var code = "class C { public int Visible; public void M() { } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_APrivateConstructor()
    {
        // Different syntax node entirely (ConstructorDeclarationSyntax) — a common, deliberate
        // pattern (static factory / singleton), not a candidate for this analyzer.
        var code = "class C { private C() { } public static C Create() => new C(); }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_NotFlag_APurelySelfRecursiveMethod()
    {
        // Documented, accepted false negative — usage detection is a plain "any identifier in the
        // type resolves to this symbol" search, which the method's own recursive call satisfies.
        var code = "class C { private void Loop() { Loop(); } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task UnusedPrivateMemberAnalyzer_Should_FlagEachUnusedMemberSeparately()
    {
        var code = "class C { private int _a; private int _b; public void M() { } }";

        var diagnostics = await GetDiagnosticsAsync(new UnusedPrivateMemberAnalyzer(), code);

        var unused = diagnostics.Where(d => d.Id == UnusedPrivateMemberAnalyzer.DiagnosticId).ToList();
        Assert.Equal(2, unused.Count);
        Assert.Contains(unused, d => d.GetMessage().Contains("_a"));
        Assert.Contains(unused, d => d.GetMessage().Contains("_b"));
    }

    // ── Batch (pass 147) — 1-2 tests per analyzer, per explicit user direction to
    // trade individual test depth for throughput once the backlog of ~671 candidates was raised.

    [Theory]
    [InlineData("class C { public void M(uint x) { if (x < 0) { } } }", true)]
    [InlineData("class C { public void M(int x) { if (x < 0) { } } }", false)]
    public async Task UnsignedValueNeverLessThanZeroAnalyzer_Should_FlagOnlyUnsignedOperands(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UnsignedValueNeverLessThanZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UnsignedValueNeverLessThanZeroAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } finally { } } }", true)]
    [InlineData("class C { public void M() { try { } finally { System.Console.WriteLine(1); } } }", false)]
    public async Task RemoveEmptyFinallyClauseAnalyzer_Should_FlagOnlyAnEmptyBlock(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveEmptyFinallyClauseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveEmptyFinallyClauseAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { if (true) { } else { } } }", true)]
    [InlineData("class C { public void M() { if (true) { } else { System.Console.WriteLine(1); } } }", false)]
    public async Task RemoveEmptyElseClauseAnalyzer_Should_FlagOnlyAnEmptyBlock(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveEmptyElseClauseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveEmptyElseClauseAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { ~C() { } }", true)]
    [InlineData("class C { ~C() { System.Console.WriteLine(1); } }", false)]
    public async Task RemoveEmptyDestructorAnalyzer_Should_FlagOnlyAnEmptyBody(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveEmptyDestructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveEmptyDestructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { ; } }", true)]
    [InlineData("class C { public void M() { for (;;) ; } }", false)] // deliberate loop-body idiom
    public async Task RemoveEmptyStatementAnalyzer_Should_NotFlagADeliberateLoopBody(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveEmptyStatementAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveEmptyStatementAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { s.ToString(); } }", true)]
    [InlineData("class C { public void M(int i) { i.ToString(); } }", false)]
    public async Task RemoveRedundantToStringCallAnalyzer_Should_FlagOnlyAStringReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantToStringCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantToStringCallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object o) { if ((o as string) != null) { } } }", true)]
    [InlineData("class C { public void M(object o) { if (o != null) { } } }", false)]
    public async Task UseIsOperatorInsteadOfAsAndNullCheckAnalyzer_Should_FlagOnlyTheAsPattern(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseIsOperatorInsteadOfAsAndNullCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseIsOperatorInsteadOfAsAndNullCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { lock (this) { } } }", true)]
    [InlineData("class C { private readonly object _lock = new(); public void M() { lock (_lock) { } } }", false)]
    [InlineData("class C { public void M() { lock (typeof(C)) { } } }", true)]
    [InlineData("class C { public void M() { lock (\"my-lock\") { } } }", true)]
    public async Task AvoidLockOnThisAnalyzer_Should_FlagOnlyLockOnThis(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AvoidLockOnThisAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AvoidLockOnThisAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { xs.Any(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { xs.Any(); } }", false)]
    public async Task UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer_Should_FlagOnlyAnIndexableReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { xs.First(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { xs.First(); } }", false)]
    public async Task UseElementAccessInsteadOfLinqMethodAnalyzer_Should_FlagOnlyAnIndexableReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseElementAccessInsteadOfLinqMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseElementAccessInsteadOfLinqMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { if (xs.Count() > 0) { } } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { if (xs.Count() > 1) { } } }", false)]
    public async Task CallAnyInsteadOfCountAnalyzer_Should_FlagOnlyAComparisonAgainstZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CallAnyInsteadOfCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CallAnyInsteadOfCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public C() { Init(); } protected virtual void Init() { } }", true)]
    [InlineData("sealed class C { public C() { Init(); } private void Init() { } }", false)]
    public async Task DoNotCallOverridableMemberInConstructorAnalyzer_Should_NotFlag_ASealedTypeOrNonVirtualMember(
        string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DoNotCallOverridableMemberInConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DoNotCallOverridableMemberInConstructorAnalyzer.DiagnosticId));
    }

    // ── Second batch (pass 148) — same "1-2 tests per analyzer" pace as pass 147.

    [Theory]
    [InlineData("class C<T> { public bool M(T t) => t == null; }", true)]
    [InlineData("class C<T> where T : class { public bool M(T t) => t == null; }", false)]
    public async Task UnconstrainedTypeParameterCheckedForNullAnalyzer_Should_FlagOnlyWhenUnconstrained(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UnconstrainedTypeParameterCheckedForNullAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UnconstrainedTypeParameterCheckedForNullAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(bool flag) { if (flag == true) { } } }", true)]
    [InlineData("class C { public void M(bool? flag) { if (flag == true) { } } }", false)] // nullable bool — not equivalent to the bare value
    public async Task SimplifyBooleanComparisonAnalyzer_Should_NotFlag_ANullableBool(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SimplifyBooleanComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SimplifyBooleanComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class Base { } class C : Base { public C() : base() { } }", true)]
    [InlineData("class Base { public Base(int x) { } } class C : Base { public C() : base(1) { } }", false)]
    public async Task RemoveRedundantBaseConstructorCallAnalyzer_Should_FlagOnlyTheArgumentLessForm(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantBaseConstructorCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantBaseConstructorCallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int i) { var x = (int)i; } }", true)]
    [InlineData("class C { public void M(long i) { var x = (int)i; } }", false)]
    public async Task RedundantCastAnalyzer_Should_FlagOnlyAnIdentityConversion(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantCastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantCastAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object o) { var x = o as object; } }", true)]
    [InlineData("class C { public void M(object o) { var x = o as string; } }", false)]
    public async Task RedundantAsOperatorAnalyzer_Should_FlagOnlyAnIdentityConversion(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantAsOperatorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantAsOperatorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class Base { public virtual void M(int x) { } } class C : Base { public override void M(int x) { base.M(x); } }", true)]
    [InlineData("class Base { public virtual void M(int x) { } } class C : Base { public override void M(int x) { base.M(x); System.Console.WriteLine(1); } }", false)]
    public async Task RemoveRedundantOverridingMemberAnalyzer_Should_NotFlag_AnOverrideWithExtraLogic(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantOverridingMemberAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantOverridingMemberAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public int Foo => Foo; }", true)]
    [InlineData("class C { private int _foo; public int Foo => _foo; }", false)]
    public async Task SelfReferencingPropertyAnalyzer_Should_FlagOnlyASelfReturningGetter(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SelfReferencingPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SelfReferencingPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class Foo { } class C { public void M() { var x = new Foo() { }; } }", true)]
    [InlineData("class Foo { public int X; } class C { public void M() { var x = new Foo() { X = 1 }; } }", false)]
    public async Task RemoveRedundantEmptyObjectInitializerAnalyzer_Should_FlagOnlyAnEmptyInitializer(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantEmptyObjectInitializerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantEmptyObjectInitializerAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int x) { if (x == 1) { } else if (x == 1) { } } }", true)]
    [InlineData("class C { public void M(int x) { if (x == 1) { } else if (x == 2) { } } }", false)]
    public async Task DuplicateIfElseIfConditionAnalyzer_Should_FlagOnlyAStructurallyIdenticalCondition(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DuplicateIfElseIfConditionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DuplicateIfElseIfConditionAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } finally { throw new System.Exception(); } } }", true)]
    [InlineData("class C { public void M() { try { } catch { throw; } } }", false)]
    public async Task DoNotThrowFromFinallyAnalyzer_Should_NotFlag_ARethrowInsideACatch(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DoNotThrowFromFinallyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DoNotThrowFromFinallyAnalyzer.DiagnosticId));
    }

    // ── Third batch (pass 149) — same pace as passes 147/148.

    [Theory]
    [InlineData("using System; class C { public void On() { } public void M() { Action a = new Action(On); } }", true)]
    [InlineData("using System; class C { public void M() { Action a = () => { }; } }", false)]
    public async Task RemoveRedundantDelegateCreationAnalyzer_Should_FlagOnlyExplicitDelegateConstruction(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantDelegateCreationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantDelegateCreationAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var x = $\"{s}\"; } }", true)]
    [InlineData("class C { public void M(string s) { var x = $\"prefix {s}\"; } }", false)]
    public async Task UnnecessaryInterpolatedStringAnalyzer_Should_FlagOnlyASoleAlreadyStringInterpolation(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UnnecessaryInterpolatedStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UnnecessaryInterpolatedStringAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { s.StartsWith(\"x\"); } }", true)]
    [InlineData("class C { public void M(string s) { s.StartsWith(\"x\", System.StringComparison.Ordinal); } }", false)]
    public async Task CultureSensitiveStringMethodAnalyzer_Should_FlagOnlyTheSingleStringArgOverload(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveStringMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CultureSensitiveStringMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int a, int b) { object.ReferenceEquals(a, b); } }", true)]
    [InlineData("class C { public void M(object a, object b) { object.ReferenceEquals(a, b); } }", false)]
    public async Task DoNotUseReferenceEqualsWithValueTypeAnalyzer_Should_FlagOnlyABoxedValueTypeArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DoNotUseReferenceEqualsWithValueTypeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DoNotUseReferenceEqualsWithValueTypeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { string s = \"\"; for (int i = 0; i < 10; i++) { s += i.ToString(); } } }", true)]
    [InlineData("class C { public void M() { string s = \"\"; s += \"x\"; } }", false)]
    public async Task ConcatenateStringsInLoopAnalyzer_Should_FlagOnlyInsideALoop(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ConcatenateStringsInLoopAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ConcatenateStringsInLoopAnalyzer.DiagnosticId));
    }

    // ── Fourth batch (pass 150) — same pace as passes 147/148/149.

    [Theory]
    [InlineData("class C { public void M() { var x = new int[0]; } }", true)]
    [InlineData("class C { public void M() { var x = new int[5]; } }", false)]
    public async Task UseArrayEmptyMethodAnalyzer_Should_FlagOnlyTheBareZeroSizeForm(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseArrayEmptyMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseArrayEmptyMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) { throw ex; } } }", true)]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) { throw; } } }", false)]
    public async Task RethrowToPreserveStackTraceAnalyzer_Should_NotFlag_ABareThrow(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RethrowToPreserveStackTraceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RethrowToPreserveStackTraceAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("enum Color { Red, Green } class C { public void M() { var x = Color.Red | Color.Green; } }", true)]
    [InlineData("[System.Flags] enum Color { Red = 1, Green = 2 } class C { public void M() { var x = Color.Red | Color.Green; } }", false)]
    public async Task BitwiseOperatorOnNonFlagsEnumAnalyzer_Should_NotFlag_AFlagsEnum(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new BitwiseOperatorOnNonFlagsEnumAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == BitwiseOperatorOnNonFlagsEnumAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(double x) { if (x == double.NaN) { } } }", true)]
    [InlineData("class C { public void M(double x) { if (double.IsNaN(x)) { } } }", false)]
    public async Task CompareNaNCorrectlyAnalyzer_Should_FlagOnlyDirectNaNComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CompareNaNCorrectlyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CompareNaNCorrectlyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } catch (System.Exception) { throw; } } }", true)]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) { System.Console.WriteLine(ex); throw; } } }", false)]
    public async Task RemoveRedundantCatchThatOnlyRethrowsAnalyzer_Should_NotFlag_ACatchWithRealLogic(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantCatchThatOnlyRethrowsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantCatchThatOnlyRethrowsAnalyzer.DiagnosticId));
    }

    // ── Fifth batch (pass 151) — same pace as passes 147-150.

    [Theory]
    [InlineData("class C { public void M(string s) { s.ToUpper(); } }", true)]
    [InlineData("class C { public void M(string s) { s.ToUpperInvariant(); } }", false)]
    public async Task CultureSensitiveCaseConversionAnalyzer_Should_FlagOnlyTheNoArgOverload(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveCaseConversionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CultureSensitiveCaseConversionAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public async void Fire() { await System.Threading.Tasks.Task.Delay(1); } }", true)]
    [InlineData("using System; class C { public async void OnClick(object sender, EventArgs e) { await System.Threading.Tasks.Task.Delay(1); } }", false)]
    public async Task AvoidAsyncVoidAnalyzer_Should_NotFlag_AnEventHandlerShapedMethod(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AvoidAsyncVoidAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AvoidAsyncVoidAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var c = xs.ToList().Count; } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var c = xs.Count(); } }", false)]
    public async Task RemoveRedundantMaterializationBeforeCountAnalyzer_Should_FlagOnlyToListDotCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantMaterializationBeforeCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantMaterializationBeforeCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { if (s == null || s == \"\") { } } }", true)]
    [InlineData("class C { public void M(string s) { if (string.IsNullOrEmpty(s)) { } } }", false)]
    public async Task UseStringIsNullOrEmptyAnalyzer_Should_FlagOnlyTheSeparateNullAndEmptyChecks(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseStringIsNullOrEmptyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseStringIsNullOrEmptyAnalyzer.DiagnosticId));
    }

    // ── Sixth batch (pass 152) — same pace as passes 147-151.

    [Theory]
    [InlineData("class C { public void M(int x) { var y = x < 0 ? -x : x; } }", true)] // Math.Abs
    [InlineData("class C { public void M(int x) { var y = x > 0 ? x : x + 1; } }", false)]
    public async Task MathHelperInsteadOfConditionalAnalyzer_Should_FlagAbsShape(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MathHelperInsteadOfConditionalAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MathHelperInsteadOfConditionalAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int x, int y) { var z = x < y ? x : y; } }", true)] // Math.Min
    [InlineData("class C { public void M(int x, int y) { var z = x < y ? y : x; } }", false)] // reversed — not Min/Max
    public async Task MathHelperInsteadOfConditionalAnalyzer_Should_FlagMinMaxShape(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MathHelperInsteadOfConditionalAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MathHelperInsteadOfConditionalAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Cast<int>(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<object> xs) { var y = xs.Cast<int>(); } }", false)]
    public async Task RedundantEnumerableCastAnalyzer_Should_FlagOnlyWhenElementTypeAlreadyMatches(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantEnumerableCastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantEnumerableCastAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key) { var v = dict.ContainsKey(key) ? dict[key] : 0; } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key, string other) { var v = dict.ContainsKey(key) ? dict[other] : 0; } }", false)]
    public async Task PreferTryGetValueAnalyzer_Should_FlagOnlyWhenTheKeyMatchesOnBothSides(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTryGetValueAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTryGetValueAnalyzer.DiagnosticId));
    }

    // ── Seventh batch (pass 153) — same pace as passes 147-152.

    [Theory]
    [InlineData("class C { public void M(int a, int b) { double result = a / b; } }", true)]
    [InlineData("class C { public void M(int a, int b) { double result = (double)a / b; } }", false)]
    public async Task IntegerDivisionInFloatingPointContextAnalyzer_Should_NotFlag_WhenAnOperandIsAlreadyCast(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new IntegerDivisionInFloatingPointContextAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == IntegerDivisionInFloatingPointContextAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("enum Color { Red, Green, Blue } class C { public void M(Color c) { switch (c) { case Color.Red: break; case Color.Green: break; } } }", true)]
    [InlineData("enum Color { Red, Green } class C { public void M(Color c) { switch (c) { case Color.Red: break; case Color.Green: break; } } }", false)]
    public async Task NonExhaustiveEnumSwitchAnalyzer_Should_NotFlag_AnExhaustiveSwitch(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NonExhaustiveEnumSwitchAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NonExhaustiveEnumSwitchAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task NonExhaustiveEnumSwitchAnalyzer_Should_NotFlag_ASwitchWithADefaultCase()
    {
        var code = "enum Color { Red, Green, Blue } class C { public void M(Color c) { switch (c) { case Color.Red: break; default: break; } } }";

        var diagnostics = await GetDiagnosticsAsync(new NonExhaustiveEnumSwitchAnalyzer(), code);

        Assert.DoesNotContain(diagnostics, d => d.Id == NonExhaustiveEnumSwitchAnalyzer.DiagnosticId);
    }

    [Theory]
    [InlineData("class Base { public virtual int P { get; set; } } class C : Base { public override int P { get => base.P; set => base.P = value; } }", true)]
    [InlineData("class Base { public virtual int P { get; set; } } class C : Base { public override int P { get => base.P; set { base.P = value; System.Console.WriteLine(1); } } }", false)]
    public async Task RemoveRedundantOverridingPropertyAnalyzer_Should_NotFlag_AnOverrideWithExtraLogic(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveRedundantOverridingPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveRedundantOverridingPropertyAnalyzer.DiagnosticId));
    }

    // ── Eighth batch (pass 154) — same pace as passes 147-153.

    [Theory]
    [InlineData("class C { public void M(int a) { var s = \"x: \" + a.ToString(); } }", true)]
    [InlineData("class C { public void M(object o) { var s = o.ToString(); } }", false)] // not inside a '+' at all
    public async Task RedundantToStringInStringConcatenationAnalyzer_Should_NotFlag_AStandaloneCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantToStringInStringConcatenationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantToStringInStringConcatenationAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { if (s.Length < 0) { } } }", true)]
    [InlineData("class D { public int Count => 0; } class C { public void M(D d) { if (d.Count < 0) { } } }", false)]
    [InlineData("class C { public void M(int[] xs) { if (xs.Length >= 0) { } } }", true)]
    [InlineData("class C { public void M(int[] xs) { if (xs.Length <= 0) { } } }", false)]
    public async Task AlwaysFalseNonNegativeLengthComparisonAnalyzer_Should_NotFlag_AnUnrelatedCountProperty(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AlwaysFalseNonNegativeLengthComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AlwaysFalseNonNegativeLengthComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { private static readonly int Max = 5; }", true)]
    [InlineData("class C { private static readonly object Obj = new object(); }", false)]
    public async Task PreferConstOverStaticReadonlyAnalyzer_Should_FlagOnlyConstEligibleTypes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferConstOverStaticReadonlyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferConstOverStaticReadonlyAnalyzer.DiagnosticId));
    }

    // ── Ninth batch (pass 155) — same pace as passes 147-154.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var m = xs.OrderBy(x => x).First(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var m = xs.MinBy(x => x); } }", false)]
    public async Task PreferMinByMaxByOverOrderByFirstAnalyzer_Should_NotFlag_ADirectMinByCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferMinByMaxByOverOrderByFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferMinByMaxByOverOrderByFirstAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var f = xs.Where(x => x > 0).FirstOrDefault(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var f = xs.FirstOrDefault(x => x > 0); } }", false)]
    public async Task MergeWhereWithTerminalLinqMethodAnalyzer_Should_NotFlag_ADirectPredicateCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MergeWhereWithTerminalLinqMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MergeWhereWithTerminalLinqMethodAnalyzer.DiagnosticId));
    }

    // ── Tenth batch (pass 156) — same pace as passes 147-155.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Where(x => x > 0).Where(x => x < 10); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Where(x => x > 0).Select(x => x * 2); } }", false)]
    public async Task CombineConsecutiveLinqCallsAnalyzer_Should_NotFlag_DifferentConsecutiveMethods(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CombineConsecutiveLinqCallsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CombineConsecutiveLinqCallsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x).OrderBy(x => -x); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x).ThenBy(x => -x); } }", false)]
    public async Task OrderByFollowedByOrderByAnalyzer_Should_NotFlag_AProperThenBy(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new OrderByFollowedByOrderByAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == OrderByFollowedByOrderByAnalyzer.DiagnosticId));
    }

    // ── Eleventh batch (pass 157) — same pace as passes 147-156.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.Concat(new[] { item }); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, List<int> ys) { var y = xs.Concat(ys); } }", false)]
    public async Task UseAppendPrependInsteadOfConcatWithArrayAnalyzer_Should_NotFlag_ConcatOfTwoRealSequences(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseAppendPrependInsteadOfConcatWithArrayAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseAppendPrependInsteadOfConcatWithArrayAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<string> xs, string item) { var b = xs.FirstOrDefault(x => x == item) != null; } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<string> xs, string item) { var b = xs.Any(x => x == item); } }", false)]
    public async Task FirstOrDefaultNullCheckInsteadOfAnyAnalyzer_Should_NotFlag_ADirectAnyCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new FirstOrDefaultNullCheckInsteadOfAnyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == FirstOrDefaultNullCheckInsteadOfAnyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("enum Color { Red, Green } class C { public int M(Color c) => c switch { Color.Red => 1, Color.Green => 2, _ => 3 }; }", true)]
    [InlineData("enum Color { Red, Green, Blue } class C { public int M(Color c) => c switch { Color.Red => 1, Color.Green => 2, _ => 3 }; }", false)]
    public async Task RedundantDiscardArmInExhaustiveSwitchExpressionAnalyzer_Should_NotFlag_AGenuinelyNeededDiscard(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDiscardArmInExhaustiveSwitchExpressionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDiscardArmInExhaustiveSwitchExpressionAnalyzer.DiagnosticId));
    }

    // ── Twelfth batch (pass 158) — same pace as passes 147-157.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var s = xs.Select(x => x * 2).Sum(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var s = xs.Sum(x => x * 2); } }", false)]
    public async Task MergeSelectWithAggregateMethodAnalyzer_Should_NotFlag_ADirectSelectorCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MergeSelectWithAggregateMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MergeSelectWithAggregateMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x).Reverse(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderByDescending(x => x).Reverse(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderByDescending(x => x); } }", false)]
    public async Task UseOppositeOrderByInsteadOfReverseAnalyzer_Should_FlagBothDirections(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseOppositeOrderByInsteadOfReverseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseOppositeOrderByInsteadOfReverseAnalyzer.DiagnosticId));
    }

    // ── Thirteenth batch (pass 159) — 3 new analyzers + 3 extensions to earlier
    // ones, same pace as passes 147-158.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Select(x => x); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Select(x => x * 2); } }", false)]
    public async Task RedundantIdentitySelectAnalyzer_Should_NotFlag_ARealProjection(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantIdentitySelectAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantIdentitySelectAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, int n) { sb.Append(n.ToString()); } }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, int n) { sb.Append(n); } }", false)]
    public async Task RedundantToStringBeforeStringBuilderAppendAnalyzer_Should_NotFlag_ADirectAppendCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantToStringBeforeStringBuilderAppendAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantToStringBeforeStringBuilderAppendAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public static readonly List<int> Items = new(); }", true)]
    [InlineData("using System.Collections.Generic; class C { private static readonly List<int> Items = new(); }", false)]
    public async Task MutableReadonlyCollectionFieldAnalyzer_Should_NotFlag_APrivateField(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MutableReadonlyCollectionFieldAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MutableReadonlyCollectionFieldAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Count(x => x > 0) > 0; } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Any(x => x > 0); } }", false)]
    public async Task CallAnyInsteadOfCountAnalyzer_Should_FlagThePredicateOverloadToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CallAnyInsteadOfCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CallAnyInsteadOfCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Where(x => x > 0).Count(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Where(x => x > 0).Count() > 0; } }", false)] // deferred to RPAD0017
    public async Task MergeWhereWithTerminalLinqMethodAnalyzer_Should_NotFlag_WhenComparedToZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MergeWhereWithTerminalLinqMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MergeWhereWithTerminalLinqMethodAnalyzer.DiagnosticId));
    }

    // ── Fourteenth batch (pass 160) — same pace as passes 147-159.

    [Theory]
    [InlineData("class C { public void M(int n) { var x = System.Convert.ToInt32(n); } }", true)]
    [InlineData("class C { public void M(long n) { var x = System.Convert.ToInt32(n); } }", false)]
    public async Task RedundantIdentityConvertCallAnalyzer_Should_FlagOnlyAnIdentityConversion(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantIdentityConvertCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantIdentityConvertCallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key, int val) { if (!dict.ContainsKey(key)) { dict[key] = val; } } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key, int val) { dict.TryAdd(key, val); } }", false)]
    public async Task PreferTryAddOverContainsKeyCheckAnalyzer_Should_NotFlag_ADirectTryAddCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTryAddOverContainsKeyCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTryAddOverContainsKeyCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; enum Color { Red, Green } class C { public void M() { var y = System.Enum.GetValues(typeof(Color)).Cast<Color>(); } }", true)]
    [InlineData("enum Color { Red, Green } class C { public void M() { var y = System.Enum.GetValues<Color>(); } }", false)]
    public async Task EnumGetValuesCastAnalyzer_Should_NotFlag_ADirectGenericCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EnumGetValuesCastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EnumGetValuesCastAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(char ch) { var x = char.ToUpper(ch); } }", true)]
    [InlineData("class C { public void M(char ch) { var x = char.ToUpperInvariant(ch); } }", false)]
    public async Task CultureSensitiveCharCaseConversionAnalyzer_Should_FlagOnlyTheCultureSensitiveOverload(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveCharCaseConversionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CultureSensitiveCharCaseConversionAnalyzer.DiagnosticId));
    }

    // ── Fifteenth batch (сто шестьдесят первый проход) — same pace as проходы 147-160.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Skip(0); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Skip(2); } }", false)]
    public async Task RedundantSkipZeroAnalyzer_Should_FlagOnlyALiteralZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantSkipZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantSkipZeroAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Take(0); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Take(2); } }", false)]
    public async Task SuspiciousTakeZeroAnalyzer_Should_FlagOnlyALiteralZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SuspiciousTakeZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SuspiciousTakeZeroAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Take(3).First(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Take(0).First(); } }", false)] // n == 0 changes throw behavior
    public async Task RedundantTakeBeforeFirstAnalyzer_Should_NotFlag_ALiteralZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantTakeBeforeFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantTakeBeforeFirstAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) when (ex is System.InvalidOperationException) { } } }", true)]
    [InlineData("class C { public void M() { try { } catch (System.InvalidOperationException ex) { } } }", false)]
    public async Task PreferSpecificCatchTypeOverIsPatternAnalyzer_Should_NotFlag_ADirectCatchType(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferSpecificCatchTypeOverIsPatternAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferSpecificCatchTypeOverIsPatternAnalyzer.DiagnosticId));
    }

    // ── Sixteenth batch (сто шестьдесят второй проход) — same pace as проходы 147-161.

    [Theory]
    [InlineData("class C { public void M() { var s = string.Format(\"no placeholders\"); } }", true)]
    [InlineData("class C { public void M(int n) { var s = string.Format(\"{0}\", n); } }", false)]
    public async Task RedundantStringFormatCallAnalyzer_Should_NotFlag_AFormatStringWithAPlaceholder(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantStringFormatCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantStringFormatCallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object a) { var b = a.Equals(null); } }", true)]
    [InlineData("class C { public void M(object a) { var b = a is null; } }", false)]
    public async Task SuspiciousEqualsNullAnalyzer_Should_NotFlag_AnIsNullCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SuspiciousEqualsNullAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SuspiciousEqualsNullAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object a, object b) { var eq = a.GetHashCode() == b.GetHashCode(); } }", true)]
    [InlineData("class C { public void M(object a, object b) { var eq = a.Equals(b); } }", false)]
    public async Task UnreliableHashCodeEqualityCheckAnalyzer_Should_NotFlag_ADirectEqualsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UnreliableHashCodeEqualityCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UnreliableHashCodeEqualityCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { private readonly object _lock = new(); public void M() { lock (_lock) { } } }", true)]
    [InlineData("class C { private readonly object _lock = new(); public void M() { lock (_lock) { System.Console.WriteLine(1); } } }", false)]
    public async Task RemoveEmptyLockStatementAnalyzer_Should_FlagOnlyAnEmptyBody(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RemoveEmptyLockStatementAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RemoveEmptyLockStatementAnalyzer.DiagnosticId));
    }

    // ── Seventeenth batch (сто шестьдесят третий проход) — bigger batch per explicit user request
    // ("бери побольше анализаторов за раз"): 7 new analyzers + 1 extension in one проход.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Reverse().Reverse(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Reverse(); } }", false)]
    public async Task RedundantDoubleReverseAnalyzer_Should_FlagOnlyTheDoubledCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDoubleReverseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDoubleReverseAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Distinct().Distinct(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Distinct(); } }", false)]
    public async Task RedundantConsecutiveDistinctCallAnalyzer_Should_FlagOnlyTheDoubledCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantConsecutiveDistinctCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantConsecutiveDistinctCallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key) { var y = dict.Keys.Contains(key); } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict, string key) { var y = dict.ContainsKey(key); } }", false)]
    public async Task DictionaryKeysContainsAnalyzer_Should_NotFlag_ADirectContainsKeyCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DictionaryKeysContainsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DictionaryKeysContainsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text.RegularExpressions; class C { public void M(string input, string pattern) { var y = Regex.Match(input, pattern).Success; } }", true)]
    [InlineData("using System.Text.RegularExpressions; class C { public void M(string input, string pattern) { var y = Regex.IsMatch(input, pattern); } }", false)]
    public async Task RegexMatchSuccessAnalyzer_Should_NotFlag_ADirectIsMatchCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RegexMatchSuccessAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RegexMatchSuccessAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int n) { System.Console.Write(n + \"\\n\"); } }", true)]
    [InlineData("class C { public void M(int n) { System.Console.WriteLine(n); } }", false)]
    public async Task ConsoleWriteWithNewlineAnalyzer_Should_NotFlag_ADirectWriteLineCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ConsoleWriteWithNewlineAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ConsoleWriteWithNewlineAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int value) { var y = xs.Any(x => x == value); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int value) { var y = xs.Contains(value); } }", false)]
    public async Task PreferContainsOverAnyEqualityAnalyzer_Should_NotFlag_ADirectContainsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferContainsOverAnyEqualityAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferContainsOverAnyEqualityAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.ToList().ToArray(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.ToArray(); } }", false)]
    public async Task RedundantDoubleMaterializationAnalyzer_Should_NotFlag_ASingleMaterialization(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDoubleMaterializationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDoubleMaterializationAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Count() >= 1; } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Any(); } }", false)]
    public async Task CallAnyInsteadOfCountAnalyzer_Should_FlagTheAtLeastOneIdiomToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CallAnyInsteadOfCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CallAnyInsteadOfCountAnalyzer.DiagnosticId));
    }

    // ── Eighteenth batch (сто шестьдесят четвёртый проход) — another bigger batch (6 new + 1
    // extension) per the standing "побольше за раз" preference from проход 163.

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s.Trim().Length == 0; } }", true)]
    [InlineData("class C { public void M(string s) { var y = string.IsNullOrWhiteSpace(s); } }", false)]
    public async Task PreferIsNullOrWhiteSpaceAnalyzer_Should_NotFlag_ADirectCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsNullOrWhiteSpaceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsNullOrWhiteSpaceAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Aggregate((a, b) => a + b); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Sum(); } }", false)]
    public async Task AggregateSumAnalyzer_Should_NotFlag_ADirectSumCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AggregateSumAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AggregateSumAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public void M() { Task.Delay(1000).Wait(); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M() { Task.Delay(1000).GetAwaiter().GetResult(); } }", true)]
    [InlineData("using System.Threading; class C { public void M() { Thread.Sleep(1000); } }", false)]
    public async Task TaskDelayThenWaitAnalyzer_Should_NotFlag_ADirectThreadSleepCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TaskDelayThenWaitAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TaskDelayThenWaitAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object obj) { var y = obj.GetType().Name == \"Foo\"; } }", true)]
    [InlineData("class C { public void M(object obj) { var y = obj is string; } }", false)]
    public async Task SuspiciousTypeNameStringComparisonAnalyzer_Should_NotFlag_AnIsCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SuspiciousTypeNameStringComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SuspiciousTypeNameStringComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Sum(x => x); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Sum(x => x * 2); } }", false)]
    public async Task RedundantIdentitySelectorInAggregateAnalyzer_Should_NotFlag_ARealSelector(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantIdentitySelectorInAggregateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantIdentitySelectorInAggregateAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; using System.Threading.Tasks; class C { public async Task M() { Thread.Sleep(1000); await Task.Delay(1); } }", true)]
    [InlineData("using System.Threading; class C { public void M() { Thread.Sleep(1000); } }", false)]
    public async Task ThreadSleepInAsyncMethodAnalyzer_Should_NotFlag_ASynchronousMethod(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ThreadSleepInAsyncMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ThreadSleepInAsyncMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x).Last(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.MaxBy(x => x); } }", false)]
    public async Task PreferMinByMaxByOverOrderByFirstAnalyzer_Should_FlagLastAsMaxBy(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferMinByMaxByOverOrderByFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferMinByMaxByOverOrderByFirstAnalyzer.DiagnosticId));
    }

    // ── Nineteenth batch (сто шестьдесят пятый проход) — same "побольше за раз" pace as проходы
    // 163-164.

    [Theory]
    [InlineData("enum Color { Red, Green } class C { public void M(string s) { var y = System.Enum.Parse(typeof(Color), s); } }", true)]
    [InlineData("enum Color { Red, Green } class C { public void M(string s) { var y = System.Enum.Parse<Color>(s); } }", false)]
    public async Task PreferGenericEnumStaticMethodAnalyzer_Should_NotFlag_ADirectGenericCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferGenericEnumStaticMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferGenericEnumStaticMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = string.Join(\",\", xs.Select(x => x.ToString())); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = string.Join(\",\", xs); } }", false)]
    public async Task RedundantToStringInStringJoinAnalyzer_Should_NotFlag_ADirectJoinCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantToStringInStringJoinAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantToStringInStringJoinAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<object> os) { var y = os.Where(x => x is string).Cast<string>(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<object> os) { var y = os.OfType<string>(); } }", false)]
    public async Task PreferOfTypeOverWhereIsCastAnalyzer_Should_NotFlag_ADirectOfTypeCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOfTypeOverWhereIsCastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOfTypeOverWhereIsCastAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { private bool IsBad(int x) => x < 0; public void M(IEnumerable<int> xs) { var y = xs.All(x => !IsBad(x)); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { private bool IsBad(int x) => x < 0; public void M(IEnumerable<int> xs) { var y = xs.All(x => IsBad(x)); } }", false)]
    public async Task PreferNotAnyOverAllNegatedPredicateAnalyzer_Should_NotFlag_ADirectNegatedAnyCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferNotAnyOverAllNegatedPredicateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferNotAnyOverAllNegatedPredicateAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int n) { var y = n.ToString(\"G\"); } }", true)]
    [InlineData("class C { public void M(int n) { var y = n.ToString(); } }", false)]
    public async Task RedundantGeneralFormatStringAnalyzer_Should_NotFlag_ANoArgToString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantGeneralFormatStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantGeneralFormatStringAnalyzer.DiagnosticId));
    }

    // ── Twentieth batch (сто шестьдесят шестой проход) — same "побольше за раз" pace as проходы
    // 163-165.

    [Theory]
    [InlineData("class C { public void M() { var y = new System.Random(System.Environment.TickCount); } }", true)]
    [InlineData("class C { public void M() { var y = new System.Random(); } }", false)]
    public async Task RedundantRandomSeedAnalyzer_Should_NotFlag_AParameterlessConstructor(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantRandomSeedAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantRandomSeedAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object x) { var y = x.Equals(x); } }", true)]
    [InlineData("class C { public void M(object x, object z) { var y = x.Equals(z); } }", false)]
    [InlineData("class C { public void M(int n) { var y = n.CompareTo(n); } }", true)]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1 } class C { public bool M(Perm x) => x.HasFlag(x); }", true)]
    public async Task SelfEqualityCheckAnalyzer_Should_NotFlag_ADifferentOperand(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SelfEqualityCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SelfEqualityCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int n) { var y = int.Parse(n.ToString()); } }", true)]
    [InlineData("class C { public void M(long n) { var y = int.Parse(n.ToString()); } }", false)]
    public async Task RedundantParseToStringRoundTripAnalyzer_Should_FlagOnlyAMatchingType(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantParseToStringRoundTripAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantParseToStringRoundTripAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = double.Parse(s); } }", true)]
    [InlineData("class C { public void M(string s) { var y = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); } }", false)]
    [InlineData("using System; class C { public void M(string s) { var y = DateTime.Parse(s); } }", true)]
    [InlineData("using System; class C { public void M(string s) { var y = TimeSpan.Parse(s); } }", true)]
    public async Task CultureSensitiveNumericParseAnalyzer_Should_FlagOnlyTheSingleArgOverload(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CultureSensitiveNumericParseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CultureSensitiveNumericParseAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Aggregate(0, (acc, x) => acc + x); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Aggregate(1, (acc, x) => acc + x); } }", false)]
    public async Task AggregateSumAnalyzer_Should_FlagTheSeededOverloadWithZeroSeedToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AggregateSumAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AggregateSumAnalyzer.DiagnosticId));
    }

    // ── Twenty-first batch (сто шестьдесят седьмой проход) — same "побольше за раз" pace as проходы
    // 163-166.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.Contains(item, EqualityComparer<int>.Default); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.Contains(item); } }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public System.Collections.Generic.IEnumerable<int> M(int[] xs, int[] ys) => xs.Except(ys, EqualityComparer<int>.Default); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public System.Collections.Generic.IEnumerable<int> M(int[] xs) => xs.OrderBy(x => x).ThenBy(x => x, Comparer<int>.Default); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public HashSet<int> M(int[] xs) => xs.ToHashSet(EqualityComparer<int>.Default); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int? M(int[] xs) => xs.MinBy(x => x, Comparer<int>.Default); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public System.Collections.Generic.IEnumerable<int> M(int[] xs, int[] ys) => xs.Except(ys); }", false)]
    public async Task RedundantDefaultComparerArgumentAnalyzer_Should_NotFlag_ADirectCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDefaultComparerArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDefaultComparerArgumentAnalyzer.DiagnosticId));
    }

    // ── Seventy-first batch (двести семнадцатый проход) — one new analyzer, three forms.

    [Theory]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.DefaultIfEmpty().First(); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.DefaultIfEmpty().Last(); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.DefaultIfEmpty().Single(); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.FirstOrDefault(); }", false)]
    public async Task PreferOrDefaultOverDefaultIfEmptyAnalyzer_Should_NotFlag_ADirectOrDefaultCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOrDefaultOverDefaultIfEmptyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOrDefaultOverDefaultIfEmptyAnalyzer.DiagnosticId));
    }

    // ── Seventy-second batch (двести восемнадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromSeconds(0); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromTicks(0); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromSeconds(5); }", false)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.Zero; }", false)]
    public async Task PreferTimeSpanZeroOverFromXWithZeroAnalyzer_Should_NotFlag_ANonZeroOrDirectZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTimeSpanZeroOverFromXWithZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTimeSpanZeroOverFromXWithZeroAnalyzer.DiagnosticId));
    }

    // ── Seventy-third batch (двести девятнадцатый проход) — one new analyzer, two forms.

    [Theory]
    [InlineData("using System; using System.Linq; class C { public int M(int[] xs) => xs.Aggregate((a, b) => Math.Max(a, b)); }", true)]
    [InlineData("using System; using System.Linq; class C { public int M(int[] xs) => xs.Aggregate((a, b) => Math.Min(a, b)); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.Max(); }", false)]
    public async Task AggregateMaxMinAnalyzer_Should_NotFlag_ADirectMaxCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AggregateMaxMinAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AggregateMaxMinAnalyzer.DiagnosticId));
    }

    // ── Seventy-fourth batch (двести двадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public bool M(int x, int y) => x.CompareTo(y) == 0; }", true)]
    [InlineData("class C { public bool M(int x, int y) => x.CompareTo(y) != 0; }", true)]
    [InlineData("class C { public bool M(System.Version v1, System.Version v2) => v1.CompareTo(v2) == 0; }", true)]
    [InlineData("class C { public bool M(System.TimeSpan t1, System.TimeSpan t2) => t1.CompareTo(t2) == 0; }", true)]
    [InlineData("class C { public bool M(System.Guid g1, System.Guid g2) => g1.CompareTo(g2) == 0; }", true)]
    [InlineData("class C { public bool M(int x, int y) => x == y; }", false)]
    public async Task PreferEqualityOperatorOverNumericCompareToAnalyzer_Should_NotFlag_ADirectEqualityCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEqualityOperatorOverNumericCompareToAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEqualityOperatorOverNumericCompareToAnalyzer.DiagnosticId));
    }

    // ── Eightieth batch (двести двадцать шестой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public int M(object a, object b) => a.GetHashCode() ^ b.GetHashCode(); }", true)]
    [InlineData("using System; class C { public int M(object a, object b) => HashCode.Combine(a, b); }", false)]
    public async Task PreferHashCodeCombineOverXorAnalyzer_Should_NotFlag_ADirectHashCodeCombineCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferHashCodeCombineOverXorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferHashCodeCombineOverXorAnalyzer.DiagnosticId));
    }

    // ── Eighty-first batch (двести двадцать седьмой проход) — two new analyzer changes: one new
    // analyzer, plus a new form on an already-shipped one.

    [Theory]
    [InlineData("using System.Threading; using System; class C { public CancellationTokenSource M(TimeSpan delay) { var cts = new CancellationTokenSource(); cts.CancelAfter(delay); return cts; } }", true)]
    [InlineData("using System.Threading; using System; class C { public CancellationTokenSource M(TimeSpan delay) { var cts = new CancellationTokenSource(); Console.WriteLine(); cts.CancelAfter(delay); return cts; } }", false)]
    [InlineData("using System.Threading; using System; class C { public CancellationTokenSource M(TimeSpan delay) => new CancellationTokenSource(delay); }", false)]
    public async Task PreferCancellationTokenSourceConstructorOverCancelAfterAnalyzer_Should_NotFlag_ANonAdjacentCancelAfter(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferCancellationTokenSourceConstructorOverCancelAfterAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferCancellationTokenSourceConstructorOverCancelAfterAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Sort(arr); Array.Reverse(arr); } }", true)]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Sort(arr); Console.WriteLine(); Array.Reverse(arr); } }", false)]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Sort(arr, (a, b) => b.CompareTo(a)); } }", false)]
    public async Task PreferDescendingComparerOverSortThenReverseAnalyzer_Should_NotFlag_ANonAdjacentArrayReverse(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDescendingComparerOverSortThenReverseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDescendingComparerOverSortThenReverseAnalyzer.DiagnosticId));
    }

    // ── Eighty-second batch (двести двадцать восьмой проход) — larger batch at the user's request
    // ("продолжай, бери сразу 10"): five new analyzers plus new forms on two already-shipped ones.

    [Theory]
    [InlineData("[System.Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm flags) => flags.HasFlag(Perm.Read) && flags.HasFlag(Perm.Write); }", true)]
    [InlineData("[System.Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm flags) => flags.HasFlag(Perm.Read | Perm.Write); }", false)]
    public async Task PreferCombinedHasFlagAnalyzer_Should_NotFlag_AnAlreadyCombinedHasFlagCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferCombinedHasFlagAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferCombinedHasFlagAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.ElementAtOrDefault(0); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.ElementAtOrDefault(1); }", false)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.FirstOrDefault(); }", false)]
    public async Task PreferFirstOrDefaultOverElementAtOrDefaultZeroAnalyzer_Should_NotFlag_ANonzeroIndexOrDirectFirstOrDefault(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferFirstOrDefaultOverElementAtOrDefaultZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferFirstOrDefaultOverElementAtOrDefaultZeroAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M(string s) => s.TrimStart().TrimEnd(); }", true)]
    [InlineData("class C { public string M(string s) => s.TrimEnd().TrimStart(); }", true)]
    [InlineData("class C { public string M(string s) => s.Trim(); }", false)]
    public async Task PreferTrimOverTrimStartThenTrimEndAnalyzer_Should_NotFlag_ADirectTrimCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTrimOverTrimStartThenTrimEndAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTrimOverTrimStartThenTrimEndAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(string s) => string.IsNullOrEmpty(s.Trim()); }", true)]
    [InlineData("class C { public bool M(string s) => string.IsNullOrWhiteSpace(s); }", false)]
    public async Task PreferIsNullOrWhiteSpaceAnalyzer_Should_NotFlag_ADirectIsNullOrWhiteSpaceCall_ForIsNullOrEmptyOfTrim(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsNullOrWhiteSpaceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsNullOrWhiteSpaceAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; class C { public bool M(int[] xs) => xs.Aggregate(true, (acc, x) => acc && x > 0); }", true)]
    [InlineData("using System.Linq; class C { public bool M(int[] xs) => xs.Aggregate(false, (acc, x) => acc || x > 0); }", true)]
    [InlineData("using System.Linq; class C { public bool M(int[] xs) => xs.All(x => x > 0); }", false)]
    public async Task AggregateAllOrAnyAnalyzer_Should_NotFlag_ADirectAllCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AggregateAllOrAnyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AggregateAllOrAnyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.Aggregate(0, (acc, x) => acc + (x > 0 ? 1 : 0)); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.Count(x => x > 0); }", false)]
    public async Task AggregateCountAnalyzer_Should_NotFlag_ADirectCountCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AggregateCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AggregateCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public string M(IEnumerable<string> xs) => xs.Aggregate(\"\", (acc, x) => acc + x); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public string M(IEnumerable<string> xs) => xs.Aggregate((a, b) => a + b); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public string M(IEnumerable<string> xs) => string.Concat(xs); }", false)]
    public async Task PreferStringConcatOverAggregateAnalyzer_Should_NotFlag_ADirectStringConcatCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferStringConcatOverAggregateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferStringConcatOverAggregateAnalyzer.DiagnosticId));
    }

    // ── Seventy-sixth batch (двести двадцать второй проход) — one new analyzer, two forms.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Sort(Comparer<int>.Default); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs, int item) => xs.BinarySearch(item, Comparer<int>.Default); }", true)]
    [InlineData("using System; using System.Collections.Generic; class C { public void M(int[] xs) => Array.Sort(xs, Comparer<int>.Default); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Sort(); }", false)]
    public async Task RedundantDefaultComparerArgumentForSortAndBinarySearchAnalyzer_Should_NotFlag_ADirectSortCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDefaultComparerArgumentForSortAndBinarySearchAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDefaultComparerArgumentForSortAndBinarySearchAnalyzer.DiagnosticId));
    }

    // ── Seventy-seventh batch (двести двадцать третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Diagnostics; class C { public void M() { var sw = new Stopwatch(); sw.Start(); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { var sw = new Stopwatch(); System.Console.WriteLine(); sw.Start(); } }", false)]
    [InlineData("using System.Diagnostics; class C { public void M() { var sw = Stopwatch.StartNew(); } }", false)]
    public async Task PreferStopwatchStartNewOverConstructorThenStartAnalyzer_Should_NotFlag_ANonAdjacentStart(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferStopwatchStartNewOverConstructorThenStartAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferStopwatchStartNewOverConstructorThenStartAnalyzer.DiagnosticId));
    }

    // ── Seventy-eighth batch (двести двадцать четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.IO; class C { public string M(string a, string b, string c) => Path.Combine(Path.Combine(a, b), c); }", true)]
    [InlineData("using System.IO; class C { public string M(string a, string b, string c) => Path.Combine(a, b, c); }", false)]
    public async Task PreferFlattenedPathCombineAnalyzer_Should_NotFlag_AFlatCombineCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferFlattenedPathCombineAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferFlattenedPathCombineAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.GroupBy(x => x % 2).Select(g => g.Key); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Select(x => x % 2).Distinct(); } }", false)]
    public async Task PreferDistinctOverGroupByKeyAnalyzer_Should_NotFlag_ADirectSelectDistinctCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDistinctOverGroupByKeyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDistinctOverGroupByKeyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Last(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Last(); } }", false)]
    public async Task UseElementAccessInsteadOfLinqMethodAnalyzer_Should_FlagLastOnAnIndexableReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseElementAccessInsteadOfLinqMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseElementAccessInsteadOfLinqMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("enum Color { Red, Green } class C { public void M(int v) { var y = System.Enum.GetName(typeof(Color), v); } }", true)]
    [InlineData("enum Color { Red, Green } class C { public void M(Color v) { var y = System.Enum.GetName<Color>(v); } }", false)]
    public async Task PreferGenericEnumStaticMethodAnalyzer_Should_FlagGetNameToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferGenericEnumStaticMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferGenericEnumStaticMethodAnalyzer.DiagnosticId));
    }

    // ── Twenty-second batch (сто шестьдесят восьмой проход) — минимум 10 анализаторов за проход по
    // явному запросу пользователя.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.ToList().Contains(item); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.Contains(item); } }", false)]
    public async Task RedundantMaterializationBeforeContainsAnalyzer_Should_NotFlag_ADirectContainsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantMaterializationBeforeContainsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantMaterializationBeforeContainsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.IndexOf(item) >= 0; } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int item) { var y = xs.Contains(item); } }", false)]
    public async Task PreferContainsOverIndexOfComparisonAnalyzer_Should_NotFlag_ADirectContainsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferContainsOverIndexOfComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferContainsOverIndexOfComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s.ToCharArray()[0]; } }", true)]
    [InlineData("class C { public void M(string s) { var y = s[0]; } }", false)]
    public async Task RedundantToCharArrayIndexingAnalyzer_Should_NotFlag_ADirectStringIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantToCharArrayIndexingAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantToCharArrayIndexingAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(double x) { var y = System.Math.Pow(x, 2); } }", true)]
    [InlineData("class C { public void M(double x) { var y = System.Math.Pow(x, 3); } }", false)]
    public async Task PreferDirectMathOperationOverPowAnalyzer_Should_NotFlag_AnUnrelatedExponent(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDirectMathOperationOverPowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDirectMathOperationOverPowAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int item) { xs.Insert(xs.Count, item); } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int item) { xs.Insert(0, item); } }", false)]
    public async Task PreferListAddOverInsertAtCountAnalyzer_Should_NotFlag_AnInsertAtAnotherIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferListAddOverInsertAtCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferListAddOverInsertAtCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) { throw new System.Exception(ex.Message); } } }", true)]
    [InlineData("class C { public void M() { try { } catch (System.Exception ex) { throw new System.Exception(ex.Message, ex); } } }", false)]
    public async Task LossyExceptionWrapAnalyzer_Should_NotFlag_WhenTheOriginalIsPassedAsInnerException(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new LossyExceptionWrapAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == LossyExceptionWrapAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public async Task M(Task t1, Task t2) { await Task.WhenAll(new[] { t1, t2 }); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public async Task M(Task t1, Task t2) { await Task.WhenAll(t1, t2); } }", false)]
    public async Task RedundantParamsArrayForTaskWhenAllAnalyzer_Should_NotFlag_ADirectParamsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantParamsArrayForTaskWhenAllAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantParamsArrayForTaskWhenAllAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Take(xs.Count()); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Take(3); } }", false)]
    public async Task RedundantTakeCountAnalyzer_Should_NotFlag_ALiteralCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantTakeCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantTakeCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s.Contains(\"x\"); } }", true)]
    [InlineData("class C { public void M(string s) { var y = s.Contains(\"xy\"); } }", false)]
    public async Task PreferCharOverloadForSingleCharStringAnalyzer_Should_NotFlag_AMultiCharLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferCharOverloadForSingleCharStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferCharOverloadForSingleCharStringAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s + \"\"; } }", true)]
    [InlineData("class C { public void M(int n) { var y = n + \"\"; } }", false)]
    public async Task RedundantStringConcatWithEmptyAnalyzer_Should_NotFlag_ANumberToStringIdiom(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantStringConcatWithEmptyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantStringConcatWithEmptyAnalyzer.DiagnosticId));
    }

    // ── Twenty-third batch (сто шестьдесят девятый проход) — four new analyzers + two extensions.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x.ToString()).ThenBy(x => x.ToString()); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.OrderBy(x => x.ToString()).ThenBy(x => x); } }", false)]
    public async Task RedundantThenByWithSameKeyAnalyzer_Should_NotFlag_ADifferentKey(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantThenByWithSameKeyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantThenByWithSameKeyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public async Task M() { await Task.Delay(0); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public async Task M() { await Task.Yield(); } }", false)]
    public async Task PreferTaskYieldOverZeroDelayAnalyzer_Should_NotFlag_ADirectYieldCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTaskYieldOverZeroDelayAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTaskYieldOverZeroDelayAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { xs.OrderBy(x => x).ToList().Sort(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var sorted = xs.OrderBy(x => x).ToList(); } }", false)]
    public async Task PointlessOrderByToListSortAnalyzer_Should_NotFlag_WhenTheResultIsCaptured(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PointlessOrderByToListSortAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PointlessOrderByToListSortAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int[] arr, int item) { var y = System.Array.IndexOf(arr, item) >= 0; } }", true)]
    [InlineData("using System.Linq; class C { public void M(int[] arr, int item) { var y = arr.Contains(item); } }", false)]
    public async Task PreferContainsOverIndexOfComparisonAnalyzer_Should_FlagTheStaticArrayFormToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferContainsOverIndexOfComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferContainsOverIndexOfComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<object> os) { var y = os.Select(x => x as string).Where(x => x != null); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<object> os) { var y = os.OfType<string>(); } }", false)]
    public async Task PreferOfTypeOverWhereIsCastAnalyzer_Should_FlagTheSelectAsWhereNotNullShapeToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOfTypeOverWhereIsCastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOfTypeOverWhereIsCastAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { private Task Bar() => Task.CompletedTask; public async Task M() { await Task.Run(async () => await Bar()); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { private Task Bar() => Task.CompletedTask; public async Task M() { await Task.Run(Bar); } }", false)]
    public async Task RedundantAsyncTaskRunWrapperAnalyzer_Should_NotFlag_ADirectMethodGroupCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantAsyncTaskRunWrapperAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantAsyncTaskRunWrapperAnalyzer.DiagnosticId));
    }

    // ── Twenty-fourth batch (сто семидесятый проход) — three new analyzers + two extensions.

    [Theory]
    [InlineData("class C { public void M(object x) { var y = x is int || x is long; } }", true)]
    [InlineData("class C { public void M(object x) { var y = x is int or long; } }", false)]
    public async Task PreferPatternCombinatorOverOrExpressionAnalyzer_Should_NotFlag_ADirectOrPattern(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferPatternCombinatorOverOrExpressionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferPatternCombinatorOverOrExpressionAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(System.IDisposable d) { if (d == null) { } else { d.Dispose(); } } }", true)]
    [InlineData("class C { public void M(System.IDisposable d) { if (d != null) { d.Dispose(); } } }", false)]
    public async Task InvertibleEmptyIfWithElseAnalyzer_Should_NotFlag_AnAlreadyInvertedIf(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvertibleEmptyIfWithElseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvertibleEmptyIfWithElseAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.Count(v => v > 0) == xs.Count(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var y = xs.All(v => v > 0); } }", false)]
    public async Task PreferAllOverCountComparisonAnalyzer_Should_NotFlag_ADirectAllCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferAllOverCountComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferAllOverCountComparisonAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s != null && s.Length > 0; } }", true)]
    [InlineData("class C { public void M(string s) { var y = !string.IsNullOrEmpty(s); } }", false)]
    public async Task UseStringIsNullOrEmptyAnalyzer_Should_FlagTheNegatedAndFormToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new UseStringIsNullOrEmptyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == UseStringIsNullOrEmptyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { var y = s != null && s.Trim().Length > 0; } }", true)]
    [InlineData("class C { public void M(string s) { var y = !string.IsNullOrWhiteSpace(s); } }", false)]
    public async Task PreferIsNullOrWhiteSpaceAnalyzer_Should_FlagTheNegatedAndFormToo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsNullOrWhiteSpaceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsNullOrWhiteSpaceAnalyzer.DiagnosticId));
    }

    // ── Twenty-fifth batch (сто семьдесят первый проход) — five new analyzers.

    [Theory]
    [InlineData("class C { public void M(int? x, int y) { var z = x.HasValue && x.Value == y; } }", true)]
    [InlineData("class C { public void M(int? x, int y) { var z = x == y; } }", false)]
    public async Task PreferNullableComparisonOverHasValueAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferNullableComparisonOverHasValueAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferNullableComparisonOverHasValueAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict) { var y = dict.Keys.Count; } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<string, int> dict) { var y = dict.Count; } }", false)]
    public async Task RedundantKeysCountAnalyzer_Should_NotFlag_ADirectCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantKeysCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantKeysCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { for (int i = 0; i <= xs.Count; i++) { var v = xs[i]; } } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { for (int i = 0; i < xs.Count; i++) { var v = xs[i]; } } }", false)]
    public async Task OffByOneLoopBoundAnalyzer_Should_NotFlag_AProperLessThanBound(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new OffByOneLoopBoundAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == OffByOneLoopBoundAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int n) { var y = xs.Skip(n).First(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int n) { var y = xs[n]; } }", false)]
    public async Task PreferElementAccessOverSkipFirstAnalyzer_Should_NotFlag_ADirectIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferElementAccessOverSkipFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferElementAccessOverSkipFirstAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Reverse().First(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { var y = xs.Last(); } }", false)]
    public async Task PreferLastOverReverseFirstAnalyzer_Should_NotFlag_ADirectLastCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferLastOverReverseFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferLastOverReverseFirstAnalyzer.DiagnosticId));
    }

    // ── Twenty-sixth batch (сто семьдесят второй проход) — four new analyzers.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { xs.ToList().Sort(); } }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { var sorted = xs.ToList(); sorted.Sort(); } }", false)]
    public async Task PointlessMaterializeThenMutateAnalyzer_Should_NotFlag_WhenTheResultIsCaptured(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PointlessMaterializeThenMutateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PointlessMaterializeThenMutateAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(double x) { var y = System.Math.Round(x, 0); } }", true)]
    [InlineData("class C { public void M(double x) { var y = System.Math.Round(x, 2); } }", false)]
    public async Task RedundantMathRoundDigitsAnalyzer_Should_NotFlag_ANonZeroDigitsArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantMathRoundDigitsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantMathRoundDigitsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text.RegularExpressions; class C { public void M(string pattern) { var y = new Regex(pattern, RegexOptions.None); } }", true)]
    [InlineData("using System.Text.RegularExpressions; class C { public void M(string pattern) { var y = new Regex(pattern); } }", false)]
    public async Task RedundantRegexOptionsNoneAnalyzer_Should_NotFlag_ADirectConstructorCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantRegexOptionsNoneAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantRegexOptionsNoneAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(char c) { var y = char.IsLetter(c) || char.IsDigit(c); } }", true)]
    [InlineData("class C { public void M(char c) { var y = char.IsLetterOrDigit(c); } }", false)]
    public async Task PreferIsLetterOrDigitAnalyzer_Should_NotFlag_ADirectCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsLetterOrDigitAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsLetterOrDigitAnalyzer.DiagnosticId));
    }

    // ── Twenty-seventh batch (сто семьдесят третий проход) — four new analyzers.

    [Theory]
    [InlineData("class C { public void M(object x) { var y = string.Format(\"{0}\", x); } }", true)]
    [InlineData("class C { public void M(object x) { var y = $\"{x}\"; } }", false)]
    public async Task RedundantSingleArgStringFormatAnalyzer_Should_NotFlag_ADirectInterpolation(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantSingleArgStringFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantSingleArgStringFormatAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(object x, object y) { var z = !(x == y); } }", true)]
    [InlineData("class C { public void M(object x, object y) { var z = x != y; } }", false)]
    public async Task PreferNotEqualsOverNegatedEqualsAnalyzer_Should_NotFlag_ADirectNotEquals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferNotEqualsOverNegatedEqualsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferNotEqualsOverNegatedEqualsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; using System.Threading.Tasks; class C { public async Task M(SemaphoreSlim sem) { sem.Wait(); await Task.Delay(1); } }", true)]
    [InlineData("using System.Threading; class C { public void M(SemaphoreSlim sem) { sem.Wait(); } }", false)]
    public async Task SemaphoreWaitInAsyncMethodAnalyzer_Should_NotFlag_ASynchronousMethod(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SemaphoreWaitInAsyncMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SemaphoreWaitInAsyncMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public async Task M(Task t) { await t.ConfigureAwait(true); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public async Task M(Task t) { await t.ConfigureAwait(false); } }", false)]
    public async Task RedundantConfigureAwaitTrueAnalyzer_Should_NotFlag_ConfigureAwaitFalse(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantConfigureAwaitTrueAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantConfigureAwaitTrueAnalyzer.DiagnosticId));
    }

    // ── Twenty-eighth batch (сто семьдесят четвёртый проход) — five new analyzers.

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public async Task<int> M(Task<int> t) { return t.Result; } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public async Task<int> M(Task<int> t) { return await t; } }", false)]
    public async Task TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer_Should_NotFlag_AnAwait(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M() { System.GC.Collect(); } }", true)]
    [InlineData("class C { public void M() { System.GC.SuppressFinalize(this); } }", false)]
    public async Task ManualGCCollectAnalyzer_Should_NotFlag_OtherGCMethods(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ManualGCCollectAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ManualGCCollectAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public void M() { for (int i = 0; i < 10; i++) { var r = new Random(); r.Next(); } } }", true)]
    [InlineData("using System; class C { public void M() { var r = new Random(); for (int i = 0; i < 10; i++) { r.Next(); } } }", false)]
    public async Task RandomInstantiatedInLoopAnalyzer_Should_NotFlag_ARandomHoistedOutsideTheLoop(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RandomInstantiatedInLoopAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RandomInstantiatedInLoopAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs.AsEnumerable(); } ", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(List<int> xs) => xs.AsEnumerable(); } ", false)]
    public async Task RedundantAsEnumerableAnalyzer_Should_NotFlag_AMoreSpecificReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantAsEnumerableAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantAsEnumerableAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(object x) { return object.Equals(x, null); } }", true)]
    [InlineData("class C { public bool M(object x) { return x is null; } }", false)]
    public async Task PreferIsNullOverStaticObjectEqualsAnalyzer_Should_NotFlag_AnIsNullPattern(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsNullOverStaticObjectEqualsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsNullOverStaticObjectEqualsAnalyzer.DiagnosticId));
    }

    // ── Twenty-ninth batch (сто семьдесят пятый проход) — four new analyzers.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int x) { if (xs.Contains(x)) xs.Remove(x); } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int x) { xs.Remove(x); } }", false)]
    public async Task PreferDirectRemoveOverContainsCheckAnalyzer_Should_NotFlag_ADirectRemove(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDirectRemoveOverContainsCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDirectRemoveOverContainsCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(bool b) => !!b; }", true)]
    [InlineData("class C { public bool M(bool b) => !b; }", false)]
    public async Task DoubleNegationAnalyzer_Should_NotFlag_ASingleNegation(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DoubleNegationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DoubleNegationAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public bool M(string a, string b) => string.Equals(a, b, StringComparison.Ordinal); } ", true)]
    [InlineData("class C { public bool M(string a, string b) => a == b; } ", false)]
    public async Task PreferEqualityOperatorOverOrdinalStringEqualsAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEqualityOperatorOverOrdinalStringEqualsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEqualityOperatorOverOrdinalStringEqualsAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(string a, string b) => a.ToLowerInvariant() == b.ToLowerInvariant(); } ", true)]
    [InlineData("using System; class C { public bool M(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase); } ", false)]
    public async Task PreferOrdinalIgnoreCaseOverInvariantCaseComparisonAnalyzer_Should_NotFlag_AnAlreadySimplifiedComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOrdinalIgnoreCaseOverInvariantCaseComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOrdinalIgnoreCaseOverInvariantCaseComparisonAnalyzer.DiagnosticId));
    }

    // ── Thirtieth batch (сто семьдесят шестой проход) — three new analyzers + one extension.

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public int M() => Task.FromResult(1).Result; } ", true)]
    [InlineData("class C { public int M() => 1; } ", false)]
    public async Task RedundantTaskFromResultThenResultAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantTaskFromResultThenResultAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantTaskFromResultThenResultAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("enum Color { Red, Green } class C { public bool M(Color c) => c.ToString() == \"Red\"; }", true)]
    [InlineData("enum Color { Red, Green } class C { public bool M(Color c) => c == Color.Red; }", false)]
    public async Task PreferDirectEnumComparisonOverToStringAnalyzer_Should_NotFlag_ADirectEnumComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDirectEnumComparisonOverToStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDirectEnumComparisonOverToStringAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public int M(int x) => x - x; } ", true)]
    [InlineData("class C { public int M(int x, int y) => x - y; } ", false)]
    public async Task SelfSubtractionOrXorAnalyzer_Should_NotFlag_ADifferentOperand(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SelfSubtractionOrXorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SelfSubtractionOrXorAnalyzer.DiagnosticId));
    }

    // ── Thirty-first batch (сто семьдесят седьмой проход) — four new analyzers.

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { Task M() => Task.CompletedTask; void N() { M(); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { Task M() => Task.CompletedTask; async Task N() { await M(); } }", false)]
    public async Task DiscardedAsyncTaskResultAnalyzer_Should_NotFlag_AnAwaitedCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DiscardedAsyncTaskResultAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DiscardedAsyncTaskResultAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { xs.ToList().ForEach(x => Console.WriteLine(x)); } }", true)]
    [InlineData("using System; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { foreach (var x in xs) Console.WriteLine(x); } }", false)]
    public async Task PreferForeachOverToListForEachAnalyzer_Should_NotFlag_ADirectForeach(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferForeachOverToListForEachAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferForeachOverToListForEachAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) { for (int i = 0; i < xs.Count(); i++) { } } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { for (int i = 0; i < xs.Count; i++) { } } }", false)]
    public async Task PreferHoistedCountOverLinqCountInLoopConditionAnalyzer_Should_NotFlag_AnO1CountProperty(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferHoistedCountOverLinqCountInLoopConditionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferHoistedCountOverLinqCountInLoopConditionAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string x) { if (x != null) { x.ToString(); } } }", true)]
    [InlineData("class C { public void M(string x) { x?.ToString(); } }", false)]
    public async Task PreferNullConditionalOverIfNullCheckAnalyzer_Should_NotFlag_AnAlreadyNullConditionalCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferNullConditionalOverIfNullCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferNullConditionalOverIfNullCheckAnalyzer.DiagnosticId));
    }

    // ── Thirty-second batch (сто семьдесят восьмой проход) — four new analyzers.

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { async Task M(Task t) { t.Wait(); await Task.Delay(1); } }", true)]
    [InlineData("using System.Threading.Tasks; class C { void M(Task t) { t.Wait(); } }", false)]
    public async Task TaskWaitInAsyncMethodAnalyzer_Should_NotFlag_ASynchronousMethod(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TaskWaitInAsyncMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TaskWaitInAsyncMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(object x) => typeof(string).IsInstanceOfType(x); }", true)]
    [InlineData("class C { public bool M(object x) => x is string; }", false)]
    public async Task PreferIsOperatorOverIsInstanceOfTypeAnalyzer_Should_NotFlag_ADirectIsCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIsOperatorOverIsInstanceOfTypeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIsOperatorOverIsInstanceOfTypeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(IEnumerable<int> xs) => xs.OrderBy(x => x).ToDictionary(x => x, x => x); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(IEnumerable<int> xs) => xs.ToDictionary(x => x, x => x); }", false)]
    public async Task PointlessOrderByBeforeToDictionaryAnalyzer_Should_NotFlag_AnUnorderedSource(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PointlessOrderByBeforeToDictionaryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PointlessOrderByBeforeToDictionaryAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(int x) => System.Math.Abs(x) == 0; }", true)]
    [InlineData("class C { public bool M(int x) => x == 0; }", false)]
    public async Task RedundantMathAbsInZeroComparisonAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantMathAbsInZeroComparisonAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantMathAbsInZeroComparisonAnalyzer.DiagnosticId));
    }

    // ── Thirty-third batch (сто семьдесят девятый проход) — five new analyzers.

    [Theory]
    [InlineData("using System.Text; class C { public string M(string s) => new StringBuilder(s).ToString(); }", true)]
    [InlineData("class C { public string M(string s) => s; }", false)]
    public async Task RedundantStringBuilderConstructorThenToStringAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantStringBuilderConstructorThenToStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantStringBuilderConstructorThenToStringAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public List<int> M(IEnumerable<int> source) { var list = new List<int>(); list.AddRange(source); return list; } }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M(IEnumerable<int> source) => new List<int>(source); }", false)]
    public async Task PreferListConstructorOverAddRangeAnalyzer_Should_NotFlag_ADirectConstructor(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferListConstructorOverAddRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferListConstructorOverAddRangeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public bool M(int x) => x == 1 || x == 2; }", true)]
    [InlineData("class C { public bool M(int x) => x is 1 or 2; }", false)]
    public async Task PreferPatternCombinatorOverOrEqualityAnalyzer_Should_NotFlag_AnAlreadyCombinedPattern(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferPatternCombinatorOverOrEqualityAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferPatternCombinatorOverOrEqualityAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<(int, int)> M(IEnumerable<int> xs, IEnumerable<int> ys) => xs.Zip(ys, (a, b) => (a, b)); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<(int, int)> M(IEnumerable<int> xs, IEnumerable<int> ys) => xs.Zip(ys); }", false)]
    public async Task PreferParameterlessZipOverTupleSelectorAnalyzer_Should_NotFlag_AParameterlessZip(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferParameterlessZipOverTupleSelectorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferParameterlessZipOverTupleSelectorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; class C { public bool M(int x, int y) => new[] { x }.Contains(y); }", true)]
    // C# 14: with `using System` the array binds to MemoryExtensions.Contains (first-class spans).
    [InlineData("using System; using System.Linq; class C { public bool M(int x, int y) => new[] { x }.Contains(y); }", true)]
    [InlineData("class C { public bool M(int x, int y) => x == y; }", false)]
    public async Task PreferEqualityOverSingleElementArrayContainsAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEqualityOverSingleElementArrayContainsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEqualityOverSingleElementArrayContainsAnalyzer.DiagnosticId));
    }

    // ── Thirty-fourth batch (сто восьмидесятый проход) — three new analyzers.

    [Theory]
    [InlineData("class C { public bool M(string x) => x.Split(',').Length == 1; }", true)]
    [InlineData("class C { public bool M(string x) => !x.Contains(','); }", false)]
    public async Task PreferContainsOverSplitLengthCheckAnalyzer_Should_NotFlag_ADirectContains(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferContainsOverSplitLengthCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferContainsOverSplitLengthCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(string s) { System.Console.Out.WriteLine(s); } }", true)]
    [InlineData("class C { public void M(string s) { System.Console.WriteLine(s); } }", false)]
    public async Task PreferConsoleWriteLineOverConsoleOutWriteLineAnalyzer_Should_NotFlag_ADirectWriteLine(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferConsoleWriteLineOverConsoleOutWriteLineAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferConsoleWriteLineOverConsoleOutWriteLineAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M(string x) => x.Trim().Trim(); }", true)]
    [InlineData("class C { public string M(string x) => x.Trim(); }", false)]
    public async Task RedundantConsecutiveTrimCallAnalyzer_Should_NotFlag_ASingleTrim(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantConsecutiveTrimCallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantConsecutiveTrimCallAnalyzer.DiagnosticId));
    }

    // ── Thirty-fifth batch (сто восемьдесят первый проход) — two new analyzers, each covering two shapes.

    [Theory]
    [InlineData("class C { public bool M(bool x) { if (x) return true; else return false; } }", true)]
    [InlineData("class C { public bool M(bool x) { bool y; if (x) { y = true; } else { y = false; } return y; } }", true)]
    [InlineData("class C { public bool M(bool x) => x; }", false)]
    public async Task SimplifyIfElseBooleanLiteralAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SimplifyIfElseBooleanLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SimplifyIfElseBooleanLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public void M(int[] arr) { System.Array.Sort(arr, 0, arr.Length); } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, System.Collections.Generic.IComparer<int> cmp) { xs.Sort(0, xs.Count, cmp); } }", true)]
    [InlineData("class C { public void M(int[] arr) { System.Array.Sort(arr); } }", false)]
    public async Task RedundantExplicitFullRangeSortAnalyzer_Should_NotFlag_AShortSortCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantExplicitFullRangeSortAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantExplicitFullRangeSortAnalyzer.DiagnosticId));
    }

    // ── Thirty-sixth batch (сто восемьдесят второй проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Text.RegularExpressions; class C { public bool M(string input, string pattern) => new Regex(pattern).IsMatch(input); }", true)]
    [InlineData("using System.Text.RegularExpressions; class C { public bool M(string input, string pattern) => Regex.IsMatch(input, pattern); }", false)]
    public async Task PreferStaticRegexMethodOverNewRegexInstanceAnalyzer_Should_NotFlag_AStaticCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferStaticRegexMethodOverNewRegexInstanceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferStaticRegexMethodOverNewRegexInstanceAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public int M() => Thread.CurrentThread.ManagedThreadId; }", true)]
    [InlineData("class C { public int M() => System.Environment.CurrentManagedThreadId; }", false)]
    public async Task PreferEnvironmentCurrentManagedThreadIdAnalyzer_Should_NotFlag_ADirectProperty(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEnvironmentCurrentManagedThreadIdAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEnvironmentCurrentManagedThreadIdAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M() => System.AppDomain.CurrentDomain.BaseDirectory; }", true)]
    [InlineData("class C { public string M() => System.AppContext.BaseDirectory; }", false)]
    public async Task PreferAppContextBaseDirectoryAnalyzer_Should_NotFlag_ADirectProperty(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferAppContextBaseDirectoryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferAppContextBaseDirectoryAnalyzer.DiagnosticId));
    }

    // ── Thirty-seventh batch (сто восемьдесят третий проход) — two new analyzers + one extension.

    [Theory]
    [InlineData("using System.Net.Http; class C { public void M() { for (int i = 0; i < 10; i++) { var c = new HttpClient(); c.Dispose(); } } }", true)]
    [InlineData("using System.Net.Http; class C { public void M(HttpClient shared) { for (int i = 0; i < 10; i++) { shared.GetAsync(\"/\"); } } }", false)]
    public async Task HttpClientInstantiatedInLoopAnalyzer_Should_NotFlag_AReusedClient(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new HttpClientInstantiatedInLoopAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == HttpClientInstantiatedInLoopAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public string M(string s) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)); }", true)]
    [InlineData("class C { public string M(string s) => s; }", false)]
    public async Task RedundantEncodingRoundTripAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantEncodingRoundTripAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantEncodingRoundTripAnalyzer.DiagnosticId));
    }

    // ── Thirty-eighth batch (сто восемьдесят четвёртый проход) — three new analyzers.

    [Theory]
    [InlineData("using System; class C { public byte[] M(byte[] bytes) => Convert.FromBase64String(Convert.ToBase64String(bytes)); }", true)]
    [InlineData("class C { public byte[] M(byte[] bytes) => bytes; }", false)]
    public async Task RedundantBase64RoundTripAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantBase64RoundTripAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantBase64RoundTripAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public string M(byte[] bytes) => BitConverter.ToString(bytes).Replace(\"-\", \"\"); }", true)]
    [InlineData("using System; class C { public string M(byte[] bytes) => Convert.ToHexString(bytes); }", false)]
    public async Task PreferConvertToHexStringOverBitConverterReplaceAnalyzer_Should_NotFlag_ADirectHexString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferConvertToHexStringOverBitConverterReplaceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferConvertToHexStringOverBitConverterReplaceAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public Guid M(Guid g) => Guid.Parse(g.ToString()); }", true)]
    [InlineData("using System; class C { public Guid M(Guid g) => g; }", false)]
    public async Task RedundantGuidParseToStringRoundTripAnalyzer_Should_NotFlag_ADirectValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantGuidParseToStringRoundTripAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantGuidParseToStringRoundTripAnalyzer.DiagnosticId));
    }

    // ── Thirty-ninth batch (сто восемьдесят пятый проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs.GroupBy(x => x % 2).Select(g => g.First()); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs.DistinctBy(x => x % 2); }", false)]
    public async Task PreferDistinctByOverGroupByFirstAnalyzer_Should_NotFlag_ADirectDistinctBy(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDistinctByOverGroupByFirstAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDistinctByOverGroupByFirstAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs.SelectMany(x => new[] { x }); }", true)]
    [InlineData("using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs; }", false)]
    public async Task RedundantSelectManySingleElementArrayAnalyzer_Should_NotFlag_ADirectSequence(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantSelectManySingleElementArrayAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantSelectManySingleElementArrayAnalyzer.DiagnosticId));
    }

    // ── Fortieth batch (сто восемьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.Count == 0 ? default : xs[0]; }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(List<int> xs) => xs.FirstOrDefault(); }", false)]
    public async Task PreferFirstOrDefaultOverCountCheckTernaryAnalyzer_Should_NotFlag_ADirectFirstOrDefault(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferFirstOrDefaultOverCountCheckTernaryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferFirstOrDefaultOverCountCheckTernaryAnalyzer.DiagnosticId));
    }

    // ── Forty-first batch (сто восемьдесят седьмой проход) — two new analyzers.

    [Theory]
    [InlineData("using System; class C { public DateTime M() => DateTime.Now.Date; }", true)]
    [InlineData("using System; class C { public DateTime M() => DateTime.Today; }", false)]
    public async Task PreferDateTimeTodayOverNowDateAnalyzer_Should_NotFlag_ADirectToday(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDateTimeTodayOverNowDateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDateTimeTodayOverNowDateAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public bool M(DateTime x, DateTime y) => DateTime.Compare(x, y) == 0; }", true)]
    [InlineData("using System; class C { public bool M(DateTime x, DateTime y) => x == y; }", false)]
    public async Task PreferEqualityOperatorOverDateTimeCompareAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEqualityOperatorOverDateTimeCompareAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEqualityOperatorOverDateTimeCompareAnalyzer.DiagnosticId));
    }

    // ── Forty-second batch (сто восемьдесят восьмой проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Runtime.InteropServices; class C { public bool M() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }", true)]
    [InlineData("using System; class C { public bool M() => OperatingSystem.IsWindows(); }", false)]
    public async Task PreferOperatingSystemIsWindowsOverRuntimeInformationAnalyzer_Should_NotFlag_ADirectCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOperatingSystemIsWindowsOverRuntimeInformationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOperatingSystemIsWindowsOverRuntimeInformationAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public bool M() => IntPtr.Size == 8; }", true)]
    [InlineData("using System; class C { public bool M() => Environment.Is64BitProcess; }", false)]
    public async Task PreferIs64BitProcessOverIntPtrSizeAnalyzer_Should_NotFlag_ADirectCheck(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferIs64BitProcessOverIntPtrSizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferIs64BitProcessOverIntPtrSizeAnalyzer.DiagnosticId));
    }

    // ── Forty-third batch (сто восемьдесят девятый проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Runtime.InteropServices; struct S { public int X; } class C { public int M() => Marshal.SizeOf(typeof(S)); }", true)]
    [InlineData("using System.Runtime.InteropServices; struct S { public int X; } class C { public int M() => Marshal.SizeOf<S>(); }", false)]
    public async Task PreferGenericMarshalSizeOfAnalyzer_Should_NotFlag_ADirectGenericCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferGenericMarshalSizeOfAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferGenericMarshalSizeOfAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class D { } class C { public object M() => Activator.CreateInstance(typeof(D)); }", true)]
    [InlineData("using System; class D { } class C { public D M() => Activator.CreateInstance<D>(); }", false)]
    public async Task PreferGenericActivatorCreateInstanceAnalyzer_Should_NotFlag_ADirectGenericCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferGenericActivatorCreateInstanceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferGenericActivatorCreateInstanceAnalyzer.DiagnosticId));
    }

    // ── Forty-fourth batch (сто девяностый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public bool M(string x, string y) => string.Compare(x, y, StringComparison.Ordinal) == 0; }", true)]
    [InlineData("class C { public bool M(string x, string y) => x == y; }", false)]
    public async Task PreferEqualityOperatorOverOrdinalStringCompareAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferEqualityOperatorOverOrdinalStringCompareAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferEqualityOperatorOverOrdinalStringCompareAnalyzer.DiagnosticId));
    }

    // ── Forty-fifth batch (сто девяносто первый проход) — one new analyzer, covering two shapes.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(IEnumerable<int> xs) => xs.ToDictionary(x => x, x => x); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public ILookup<int, int> M(IEnumerable<int> xs) => xs.ToLookup(x => x, x => x); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(IEnumerable<int> xs) => xs.ToDictionary(x => x); }", false)]
    public async Task RedundantIdentityValueSelectorAnalyzer_Should_NotFlag_ASingleSelectorOverload(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantIdentityValueSelectorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantIdentityValueSelectorAnalyzer.DiagnosticId));
    }

    // ── Forty-sixth batch (сто девяносто второй проход) — one new analyzer, covering two shapes.

    [Theory]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, string x) { sb.Append(x).AppendLine(); } }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) { sb.Append(System.Environment.NewLine); } }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, string x) { sb.AppendLine(x); } }", false)]
    public async Task PreferAppendLineOverAppendThenAppendLineAnalyzer_Should_NotFlag_ADirectAppendLine(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferAppendLineOverAppendThenAppendLineAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferAppendLineOverAppendThenAppendLineAnalyzer.DiagnosticId));
    }

    // ── Forty-seventh batch (сто девяносто третий проход) — one new analyzer, covering two shapes.

    [Theory]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Clear(arr, 0, arr.Length); } }", true)]
    [InlineData("using System; class C { public int M(int[] arr, int value) => Array.IndexOf(arr, value, 0, arr.Length); }", true)]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Reverse(arr, 0, arr.Length); } }", true)]
    [InlineData("using System; class C { public void M(int[] src, int[] dst) { Array.Copy(src, 0, dst, 0, src.Length); } }", true)]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Clear(arr); } }", false)]
    [InlineData("using System; class C { public void M(int[] arr) { Array.Reverse(arr); } }", false)]
    [InlineData("using System; class C { public void M(int[] src, int[] dst) { Array.Copy(src, dst, src.Length); } }", false)]
    public async Task RedundantExplicitFullRangeArrayOperationAnalyzer_Should_NotFlag_AShortCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantExplicitFullRangeArrayOperationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantExplicitFullRangeArrayOperationAnalyzer.DiagnosticId));
    }

    // ── Sixty-second batch (двести восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public bool M(string s) => bool.Parse(s.ToUpperInvariant()); }", true)]
    [InlineData("class C { public bool M(string s) => bool.TryParse(s.ToLowerInvariant(), out _); }", true)]
    [InlineData("class C { public bool M(string s) => bool.Parse(s); }", false)]
    [InlineData("class C { public bool M(string s) => bool.Parse(s.ToUpper()); }", false)]
    public async Task RedundantCaseConversionBeforeBoolParseAnalyzer_Should_NotFlag_APlainToUpperCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantCaseConversionBeforeBoolParseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantCaseConversionBeforeBoolParseAnalyzer.DiagnosticId));
    }

    // ── Forty-eighth batch (сто девяносто четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Threading; class C { void M(int x, CancellationToken ct = default) { } public void N() => M(5, CancellationToken.None); }", true)]
    [InlineData("using System.Threading; class C { void M(int x, CancellationToken ct = default) { } public void N() => M(5); }", false)]
    public async Task RedundantCancellationTokenNoneArgumentAnalyzer_Should_NotFlag_AnOmittedArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantCancellationTokenNoneArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantCancellationTokenNoneArgumentAnalyzer.DiagnosticId));
    }

    // ── Forty-ninth batch (сто девяносто пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public string M(IEnumerable<string> xs) => xs.Aggregate((a, b) => a + b); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public string M(IEnumerable<string> xs) => string.Concat(xs); }", false)]
    public async Task PreferStringConcatOverAggregateAnalyzer_Should_NotFlag_ADirectConcat(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferStringConcatOverAggregateAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferStringConcatOverAggregateAnalyzer.DiagnosticId));
    }

    // ── Fiftieth batch (сто девяносто шестой проход) — one new analyzer, covering two shapes.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(Dictionary<int, int> dict) => dict.Select(kv => kv.Key); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(Dictionary<int, int> dict) => dict.Select(kv => kv.Value); }", true)]
    [InlineData("using System.Collections.Generic; class C { public IEnumerable<int> M(Dictionary<int, int> dict) => dict.Keys; }", false)]
    public async Task PreferKeysOrValuesOverSelectAnalyzer_Should_NotFlag_ADirectKeysProperty(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferKeysOrValuesOverSelectAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferKeysOrValuesOverSelectAnalyzer.DiagnosticId));
    }

    // ── Fifty-first batch (сто девяносто седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(Dictionary<int, int> dict) => dict.ToDictionary(kv => kv.Key, kv => kv.Value); }", true)]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<int, int> M(Dictionary<int, int> dict) => new Dictionary<int, int>(dict); }", false)]
    public async Task PreferDictionaryConstructorOverToDictionaryAnalyzer_Should_NotFlag_ADirectCopyConstructor(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDictionaryConstructorOverToDictionaryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDictionaryConstructorOverToDictionaryAnalyzer.DiagnosticId));
    }

    // ── Fifty-second batch (сто девяносто восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M(char[] chars) => string.Join(\"\", chars); }", true)]
    [InlineData("class C { public string M(char[] chars) => new string(chars); }", false)]
    public async Task PreferNewStringOverJoinOnCharArrayAnalyzer_Should_NotFlag_ADirectStringConstructor(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferNewStringOverJoinOnCharArrayAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferNewStringOverJoinOnCharArrayAnalyzer.DiagnosticId));
    }

    // ── Fifty-third batch (сто девяносто девятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M(string dir, string file) => dir + \"\\\\\" + file; }", true)]
    [InlineData("class C { public string M(string dir, string file) => dir + \"/\" + file; }", true)]
    [InlineData("using System.IO; class C { public string M(string dir, string file) => Path.Combine(dir, file); }", false)]
    public async Task PreferPathCombineOverConcatenationAnalyzer_Should_NotFlag_APathCombineCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferPathCombineOverConcatenationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferPathCombineOverConcatenationAnalyzer.DiagnosticId));
    }

    // ── Fifty-fourth batch (двухсотый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public ReadOnlySpan<char> M(string s, int i, int len) => s.Substring(i, len).AsSpan(); }", true)]
    [InlineData("using System; class C { public ReadOnlySpan<char> M(string s, int i) => s.Substring(i).AsSpan(); }", true)]
    [InlineData("using System; class C { public ReadOnlySpan<char> M(string s, int i, int len) => s.AsSpan(i, len); }", false)]
    public async Task PreferDirectAsSpanOverSubstringThenAsSpanAnalyzer_Should_NotFlag_ADirectAsSpanCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDirectAsSpanOverSubstringThenAsSpanAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDirectAsSpanOverSubstringThenAsSpanAnalyzer.DiagnosticId));
    }

    // ── Fifty-fifth batch (двести первый проход) — one new analyzer, three forms.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public bool M(List<int> xs) => xs.Any(x => x > 0); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public bool M(List<int> xs) => xs.All(x => x > 0); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(List<int> xs) => xs.FirstOrDefault(x => x > 0); }", true)]
    [InlineData("using System.Collections.Generic; class C { public bool M(List<int> xs) => xs.Exists(x => x > 0); }", false)]
    public async Task PreferListSearchMethodOverLinqEquivalentAnalyzer_Should_NotFlag_ADirectListMethodCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferListSearchMethodOverLinqEquivalentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferListSearchMethodOverLinqEquivalentAnalyzer.DiagnosticId));
    }

    // ── Fifty-sixth batch (двести второй проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M(uint x) => x.ToString().PadLeft(4, '0'); }", true)]
    [InlineData("class C { public string M(int x) => x.ToString().PadLeft(4, '0'); }", false)]
    [InlineData("class C { public string M(uint x) => x.ToString(\"D4\"); }", false)]
    public async Task PreferDFormatSpecifierOverToStringPadLeftAnalyzer_Should_NotFlag_ASignedIntegerOrDirectFormat(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDFormatSpecifierOverToStringPadLeftAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDFormatSpecifierOverToStringPadLeftAnalyzer.DiagnosticId));
    }

    // ── Fifty-seventh batch (двести третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) { sb.Length = 0; } }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) { sb.Clear(); } }", false)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) { sb.Remove(0, sb.Length); } }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) { sb.Remove(1, sb.Length); } }", false)]
    public async Task PreferClearOverStringBuilderLengthZeroAnalyzer_Should_NotFlag_ADirectClearCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferClearOverStringBuilderLengthZeroAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferClearOverStringBuilderLengthZeroAnalyzer.DiagnosticId));
    }

    // ── Fifty-eighth batch (двести четвёртый проход) — one new analyzer, three forms.

    [Theory]
    [InlineData("using System.Linq; class C { public bool M(int[] xs) => xs.Any(x => x > 0); }", true)]
    [InlineData("using System.Linq; class C { public bool M(int[] xs) => xs.All(x => x > 0); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.FirstOrDefault(x => x > 0); }", true)]
    [InlineData("class C { public bool M(int[] xs) => System.Array.Exists(xs, x => x > 0); }", false)]
    public async Task PreferArraySearchMethodOverLinqEquivalentAnalyzer_Should_NotFlag_ADirectArrayMethodCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferArraySearchMethodOverLinqEquivalentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferArraySearchMethodOverLinqEquivalentAnalyzer.DiagnosticId));
    }

    // ── Fifty-ninth batch (двести пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public int M() => new Random().Next(); }", true)]
    [InlineData("using System; class C { public int M() => new Random(42).Next(); }", false)]
    [InlineData("using System; class C { public int M() => Random.Shared.Next(); }", false)]
    public async Task PreferRandomSharedOverThrowawayRandomInstanceAnalyzer_Should_NotFlag_ASeededOrSharedRandom(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferRandomSharedOverThrowawayRandomInstanceAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferRandomSharedOverThrowawayRandomInstanceAnalyzer.DiagnosticId));
    }

    // ── Sixtieth batch (двести шестой проход) — one new analyzer, two forms.

    [Theory]
    [InlineData("class C { public int M(int x, int min, int max) => System.Math.Min(System.Math.Max(x, min), max); }", true)]
    [InlineData("class C { public int M(int x, int min, int max) => System.Math.Max(System.Math.Min(x, max), min); }", true)]
    [InlineData("class C { public int M(int x, int min, int max) => System.Math.Clamp(x, min, max); }", false)]
    public async Task PreferMathClampOverNestedMinMaxAnalyzer_Should_NotFlag_ADirectClampCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferMathClampOverNestedMinMaxAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferMathClampOverNestedMinMaxAnalyzer.DiagnosticId));
    }

    // ── Sixty-third batch (двести девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { xs.Sort(); xs.Reverse(); } }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { xs.Sort(); System.Console.WriteLine(); xs.Reverse(); } }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) { xs.Sort((a, b) => b.CompareTo(a)); } }", false)]
    public async Task PreferDescendingComparerOverSortThenReverseAnalyzer_Should_NotFlag_ANonAdjacentReverse(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDescendingComparerOverSortThenReverseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDescendingComparerOverSortThenReverseAnalyzer.DiagnosticId));
    }

    // ── Sixty-fifth batch (двести одиннадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public int M(string s) => int.Parse(s, NumberStyles.Integer); }", true)]
    [InlineData("using System.Globalization; class C { public long M(string s) => long.Parse(s, NumberStyles.Integer); }", true)]
    [InlineData("class C { public int M(string s) => int.Parse(s); }", false)]
    [InlineData("using System.Globalization; class C { public double M(string s) => double.Parse(s, NumberStyles.Float); }", false)]
    public async Task RedundantDefaultNumberStylesArgumentAnalyzer_Should_NotFlag_ADirectParseCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDefaultNumberStylesArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDefaultNumberStylesArgumentAnalyzer.DiagnosticId));
    }

    // ── Sixty-sixth batch (двести двенадцатый проход) — one new analyzer, two forms.

    [Theory]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<string> M(string s) => s.Split(',').Where(x => x.Length > 0); }", true)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<string> M(string s) => s.Split(',').Where(x => x.Length != 0); }", true)]
    [InlineData("class C { public string[] M(string s) => s.Split(',', System.StringSplitOptions.RemoveEmptyEntries); }", false)]
    public async Task PreferRemoveEmptyEntriesOverWhereLengthAnalyzer_Should_NotFlag_ADirectRemoveEmptyEntries(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferRemoveEmptyEntriesOverWhereLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferRemoveEmptyEntriesOverWhereLengthAnalyzer.DiagnosticId));
    }

    // ── Sixty-seventh batch (двести тринадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<string> M(string s) => s.Split(',').Select(x => x.Trim()); }", true)]
    [InlineData("class C { public string[] M(string s) => s.Split(',', System.StringSplitOptions.TrimEntries); }", false)]
    public async Task PreferTrimEntriesOverSelectTrimAnalyzer_Should_NotFlag_ADirectTrimEntries(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferTrimEntriesOverSelectTrimAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferTrimEntriesOverSelectTrimAnalyzer.DiagnosticId));
    }

    // ── Sixty-eighth batch (двести четырнадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("enum Color { Red, Green, Blue } class C { public bool M(Color a, Color b) => a.ToString() == b.ToString(); }", true)]
    [InlineData("enum Color { Red, Green, Blue } class C { public bool M(Color a, Color b) => a.ToString() != b.ToString(); }", true)]
    [InlineData("enum Color { Red, Green, Blue } class C { public bool M(Color a, Color b) => a == b; }", false)]
    public async Task PreferDirectEnumComparisonOverToStringToStringAnalyzer_Should_NotFlag_ADirectComparison(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferDirectEnumComparisonOverToStringToStringAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferDirectEnumComparisonOverToStringToStringAnalyzer.DiagnosticId));
    }

    // ── Sixty-ninth batch (двести пятнадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M(int n) => Enumerable.Range(0, n).Select(i => 0); }", true)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M(int n) => Enumerable.Range(0, n).Select(i => i); }", false)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M(int n) => Enumerable.Repeat(0, n); }", false)]
    public async Task PreferRepeatOverRangeSelectConstantAnalyzer_Should_NotFlag_ASelectorThatUsesTheIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferRepeatOverRangeSelectConstantAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferRepeatOverRangeSelectConstantAnalyzer.DiagnosticId));
    }

    // ── Eighty-third batch (двести двадцать девятый проход) — two new analyzers: a Join-with-empty-
    // separator sibling to the existing char[]-array form, and a DateTime/DateTimeOffset/TimeSpan
    // Add/Subtract-to-operator analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public string M(List<string> xs) => string.Join(\"\", xs); }", true)]
    [InlineData("class C { public string M(char[] xs) => string.Join(\"\", xs); }", false)]
    [InlineData("using System.Collections.Generic; class C { public string M(List<string> xs) => string.Join(\", \", xs); }", false)]
    public async Task PreferStringConcatOverJoinWithEmptySeparatorAnalyzer_Should_NotFlag_ACharArrayOrNonemptySeparator(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferStringConcatOverJoinWithEmptySeparatorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferStringConcatOverJoinWithEmptySeparatorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public DateTime M(DateTime dt, TimeSpan ts) => dt.Add(ts); }", true)]
    [InlineData("using System; class C { public DateTime M(DateTime dt, TimeSpan ts) => dt.Subtract(ts); }", true)]
    [InlineData("using System; class C { public TimeSpan M(TimeSpan ts1, TimeSpan ts2) => ts1.Add(ts2); }", true)]
    [InlineData("using System; class C { public DateTimeOffset M(DateTimeOffset dt, TimeSpan ts) => dt.Add(ts); }", true)]
    [InlineData("using System; class C { public DateTime M(DateTime dt, TimeSpan ts) => dt + ts; }", false)]
    public async Task PreferOperatorOverAddSubtractMethodAnalyzer_Should_NotFlag_ADirectOperatorUse(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOperatorOverAddSubtractMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOperatorOverAddSubtractMethodAnalyzer.DiagnosticId));
    }

    // ── Eighty-fourth batch (двести тридцатый проход) — three new analyzers, survivors of a round-1
    // PROBE where four other candidates (Substring→range operator, arr[]/list[]→index-from-end operator)
    // were dropped for colliding with the built-in IDE0056/IDE0057 "Indexing/Substring can be simplified"
    // rules on the exact same span.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.ElementAt(xs.Count() - 1); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.Last(); }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs, IEnumerable<int> ys) => xs.ElementAt(ys.Count() - 1); }", false)]
    public async Task PreferLastOverElementAtCountMinusOneAnalyzer_Should_NotFlag_ADirectLastCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferLastOverElementAtCountMinusOneAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferLastOverElementAtCountMinusOneAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => xs.Concat(Enumerable.Empty<int>()); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs) => Enumerable.Empty<int>().Concat(xs); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public IEnumerable<int> M(IEnumerable<int> xs, IEnumerable<int> ys) => xs.Concat(ys); }", false)]
    public async Task RedundantConcatWithEmptyAnalyzer_Should_NotFlag_AConcatWithANonemptySequence(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantConcatWithEmptyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantConcatWithEmptyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M(int x) => System.Convert.ToString(x); }", true)]
    [InlineData("using System.Globalization; class C { public string M(double x, CultureInfo ci) => System.Convert.ToString(x, ci); }", true)]
    [InlineData("class C { public string M(int x) => x.ToString(); }", false)]
    [InlineData("class C { public string M(bool x) => System.Convert.ToString(x); }", false)]
    public async Task PreferInstanceToStringOverConvertToStringForNumericAnalyzer_Should_NotFlag_ANonNumericConvertToStringCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferInstanceToStringOverConvertToStringForNumericAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferInstanceToStringOverConvertToStringForNumericAnalyzer.DiagnosticId));
    }

    // ── Eighty-fifth batch (двести тридцать первый проход) — one new analyzer, survivor of a round-1
    // PROBE where the IndexOf(x) == 0 → StartsWith(x) idea and the double/decimal.Parse(Trim()) forms
    // were dropped: both already co-occur with existing RPAD culture-sensitivity analyzers
    // (CultureSensitiveStringMethodAnalyzer/RPAD0031, CultureSensitiveNumericParseAnalyzer/RPAD0098) on
    // the exact same call span, for an unrelated reason.

    [Theory]
    [InlineData("class C { public int M(string s) => int.Parse(s.Trim()); }", true)]
    [InlineData("class C { public bool M(string s) => bool.Parse(s.Trim()); }", true)]
    [InlineData("class C { public long M(string s, System.IFormatProvider p) => long.Parse(s.Trim(), p); }", true)]
    [InlineData("class C { public int M(string s) => int.Parse(s); }", false)]
    [InlineData("class C { public double M(string s) => double.Parse(s.Trim()); }", false)]
    [InlineData("class C { public int M(string s) => int.Parse(s.Trim(), System.Globalization.NumberStyles.Integer); }", false)]
    public async Task RedundantTrimBeforeIntegralOrBoolParseAnalyzer_Should_NotFlag_ADoubleParseOrExplicitNumberStyles(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantTrimBeforeIntegralOrBoolParseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantTrimBeforeIntegralOrBoolParseAnalyzer.DiagnosticId));
    }

    // ── Eighty-sixth batch (двести тридцать второй проход) — one new analyzer plus a new opposite-
    // polarity form on an already-shipped one.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.Any() ? xs.First() : default; }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.Any() ? xs.Last() : default; }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.Any(x => x > 0) ? xs.First(x => x > 0) : default; }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.FirstOrDefault(); }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs, IEnumerable<int> ys) => xs.Any() ? ys.First() : default; }", false)]
    public async Task PreferOrDefaultOverAnyCheckTernaryAnalyzer_Should_NotFlag_ADirectFirstOrDefaultCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferOrDefaultOverAnyCheckTernaryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferOrDefaultOverAnyCheckTernaryAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.Count == 0 ? default : xs[0]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.Count > 0 ? xs[0] : default; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.FirstOrDefault(); }", false)]
    public async Task PreferFirstOrDefaultOverCountCheckTernaryAnalyzer_Should_NotFlag_ADirectFirstOrDefaultCall_ForTheGreaterThanForm(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferFirstOrDefaultOverCountCheckTernaryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferFirstOrDefaultOverCountCheckTernaryAnalyzer.DiagnosticId));
    }

    // ── Eighty-seventh batch (двести тридцать третий проход) — two new analyzers.

    [Theory]
    [InlineData("using System; class C { public void M(string x) { if (x == null) throw new ArgumentNullException(nameof(x)); } }", true)]
    [InlineData("using System; class C { public void M(string x) { if (x is null) throw new ArgumentNullException(nameof(x)); } }", true)]
    [InlineData("using System; class C { public void M(string x) { ArgumentNullException.ThrowIfNull(x); } }", false)]
    [InlineData("using System; class C { public void M(string x, string y) { if (x == null) throw new ArgumentNullException(nameof(y)); } }", false)]
    [InlineData("using System; class C { public void M(string x, string message) { if (x == null) throw new ArgumentNullException(nameof(x), message); } }", false)]
    public async Task PreferThrowIfNullOverIfNullCheckAnalyzer_Should_NotFlag_ADirectThrowIfNullCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferThrowIfNullOverIfNullCheckAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferThrowIfNullOverIfNullCheckAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm flags) => flags.HasFlag(Perm.None); }", true)]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm flags) => flags.HasFlag(Perm.Read); }", false)]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm flags) => flags == Perm.None; }", false)]
    public async Task HasFlagWithZeroValueAnalyzer_Should_NotFlag_AHasFlagWithANonzeroValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new HasFlagWithZeroValueAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == HasFlagWithZeroValueAnalyzer.DiagnosticId));
    }

    // ── Eighty-eighth batch (двести тридцать четвёртый проход) — one new analyzer plus new forms on
    // two already-shipped ones, all in the "self-comparison / always-true-or-false" reliability family.

    [Theory]
    [InlineData("class C { public int M(int x) => System.Math.Max(x, x); }", true)]
    [InlineData("class C { public int M(int x) => System.Math.Min(x, x); }", true)]
    [InlineData("class C { public int M(int x, int y) => System.Math.Max(x, y); }", false)]
    public async Task SelfArgumentMathMaxMinAnalyzer_Should_NotFlag_ADifferentSecondArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SelfArgumentMathMaxMinAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SelfArgumentMathMaxMinAnalyzer.DiagnosticId));
    }

    // ── Eighty-ninth batch (двести тридцать пятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public bool M(bool x) => x && x; }", true)]
    [InlineData("class C { public bool M(bool x) => x || x; }", true)]
    [InlineData("class C { public int M(int x) => x & x; }", true)]
    [InlineData("class C { public int M(int x) => x | x; }", true)]
    [InlineData("class C { public bool M(bool x, bool y) => x && y; }", false)]
    public async Task SelfLogicalOrBitwiseOperatorAnalyzer_Should_NotFlag_ADifferentOperand(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SelfLogicalOrBitwiseOperatorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SelfLogicalOrBitwiseOperatorAnalyzer.DiagnosticId));
    }

    // ── Ninetieth batch (двести тридцать шестой проход) — one new analyzer plus new forms on an
    // already-shipped one, both in the "locking on the wrong target" reliability family.

    [Theory]
    [InlineData("class C { public void M() { lock (new object()) { } } }", true)]
    [InlineData("class C { public void M() { lock (new()) { } } }", true)]
    [InlineData("class C { private readonly object _lock = new(); public void M() { lock (_lock) { } } }", false)]
    public async Task LockOnNewObjectAnalyzer_Should_NotFlag_ALockOnAFieldHoldingAPreallocatedObject(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new LockOnNewObjectAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == LockOnNewObjectAnalyzer.DiagnosticId));
    }

    // ── Ninety-first batch (двести тридцать седьмой проход) — two new analyzers, both in the
    // "Monitor lock-acquisition mistake" reliability family.

    [Theory]
    [InlineData("using System.Threading; class C { object o = new(); void M() { Monitor.Enter(o); DoWork(); Monitor.Exit(o); } void DoWork() { } }", true)]
    [InlineData("using System.Threading; class C { object o = new(); void M() { Monitor.Enter(o); try { DoWork(); } finally { Monitor.Exit(o); } } void DoWork() { } }", false)]
    public async Task MonitorEnterWithoutTryFinallyAnalyzer_Should_NotFlag_AnEnterImmediatelyFollowedByTryFinally(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MonitorEnterWithoutTryFinallyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MonitorEnterWithoutTryFinallyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { object o = new(); void M() { Monitor.TryEnter(o); } }", true)]
    [InlineData("using System.Threading; class C { object o = new(); void M() { if (Monitor.TryEnter(o)) { } } }", false)]
    [InlineData("using System.Threading; class C { object o = new(); void M() { var acquired = Monitor.TryEnter(o); } }", false)]
    public async Task DiscardedMonitorTryEnterResultAnalyzer_Should_NotFlag_AnExaminedResult(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DiscardedMonitorTryEnterResultAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DiscardedMonitorTryEnterResultAnalyzer.DiagnosticId));
    }

    // ── Ninety-second batch (двести тридцать восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Threading; class C { SemaphoreSlim s = new(1); void M() { s.Wait(); DoWork(); s.Release(); } void DoWork() { } }", true)]
    [InlineData("using System.Threading; class C { SemaphoreSlim s = new(1); void M() { s.Wait(); try { DoWork(); } finally { s.Release(); } } void DoWork() { } }", false)]
    [InlineData("using System.Threading; class C { ReaderWriterLockSlim rw = new(); void M() { rw.EnterReadLock(); DoWork(); rw.ExitReadLock(); } void DoWork() { } }", true)]
    [InlineData("using System.Threading; class C { ReaderWriterLockSlim rw = new(); void M() { rw.EnterWriteLock(); try { DoWork(); } finally { rw.ExitWriteLock(); } } void DoWork() { } }", false)]
    public async Task SemaphoreOrReaderWriterLockAcquireWithoutTryFinallyAnalyzer_Should_NotFlag_AnAcquireImmediatelyFollowedByTryFinally(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SemaphoreOrReaderWriterLockAcquireWithoutTryFinallyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SemaphoreOrReaderWriterLockAcquireWithoutTryFinallyAnalyzer.DiagnosticId));
    }

    // ── Ninety-third batch (двести тридцать девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int>(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<int, int> M() => new Dictionary<int, int>(-1); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int>(4); }", false)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M(int n) => new List<int>(-n); }", false)]
    [InlineData("using System.Collections.Generic; class C { public PriorityQueue<int, int> M() => new PriorityQueue<int, int>(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public SortedList<int, int> M() => new SortedList<int, int>(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public SortedList<int, int> M() => new SortedList<int, int>(5); }", false)]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(-1); }", true)]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(5); }", false)]
    [InlineData("using System.Collections.Specialized; class C { public NameValueCollection M() => new NameValueCollection(-1); }", true)]
    [InlineData("using System.Collections.Specialized; class C { public NameValueCollection M() => new NameValueCollection(5); }", false)]
    public async Task NegativeCapacityArgumentAnalyzer_Should_NotFlag_ANonnegativeOrNonLiteralCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeCapacityArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeCapacityArgumentAnalyzer.DiagnosticId));
    }

    // ── Ninety-fourth batch (двести сороковой проход) — two new analyzers, both "literal argument that
    // always throws" reliability findings.

    [Theory]
    [InlineData("using System.Linq; class C { public void M() => Enumerable.Repeat(0, -1); }", true)]
    [InlineData("using System.Linq; class C { public void M() => Enumerable.Range(0, -1); }", true)]
    [InlineData("using System.Linq; class C { public void M() => Enumerable.Repeat(0, 5); }", false)]
    public async Task NegativeCountArgumentAnalyzer_Should_NotFlag_ANonnegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeCountArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeCountArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public void M() => Task.Delay(-2); }", true)]
    [InlineData("using System.Threading; class C { public void M() => Thread.Sleep(-2); }", true)]
    [InlineData("using System.Threading; class C { public CancellationTokenSource M() => new CancellationTokenSource(-2); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M() => Task.Delay(-1); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M() => Task.Delay(100); }", false)]
    public async Task NegativeTimeoutArgumentAnalyzer_Should_NotFlag_InfiniteOrNonnegativeTimeout(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeTimeoutArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeTimeoutArgumentAnalyzer.DiagnosticId));
    }

    // ── Ninety-fifth batch (двести сорок первый проход) — two new analyzers, both "literal argument
    // that always throws" reliability findings.

    [Theory]
    [InlineData("using System; class C { public int M(Random r) => r.Next(10, 5); }", true)]
    [InlineData("using System; class C { public int M(Random r) => r.Next(5, 10); }", false)]
    [InlineData("using System; class C { public int M(Random r) => r.Next(5, 5); }", false)]
    [InlineData("using System; class C { public int M(Random r) => r.Next(-1); }", true)]
    [InlineData("using System; class C { public int M(Random r) => r.Next(5); }", false)]
    [InlineData("using System; class C { public long M(Random r) => r.NextInt64(10, 5); }", true)]
    [InlineData("using System; class C { public long M(Random r) => r.NextInt64(5, 10); }", false)]
    [InlineData("using System; class C { public long M(Random r) => r.NextInt64(-1); }", true)]
    [InlineData("using System; class C { public long M(Random r) => r.NextInt64(5); }", false)]
    public async Task InvalidRandomRangeArgumentAnalyzer_Should_NotFlag_AValidOrEqualRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRandomRangeArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRandomRangeArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M() => \"abc\".Substring(1, 10); }", true)]
    [InlineData("class C { public string M() => \"abc\".Substring(-1, 2); }", true)]
    [InlineData("class C { public string M() => \"abc\".Substring(1, 2); }", false)]
    [InlineData("class C { public string M(string s) => s.Substring(1, 10); }", false)]
    public async Task SubstringOutOfRangeAnalyzer_Should_NotFlag_AnInRangeSubstringOrNonLiteralReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new SubstringOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == SubstringOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Ninety-sixth batch (двести сорок второй проход) — three new analyzers, all "literal argument
    // that always throws" reliability findings.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.ElementAt(-1); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M(IEnumerable<int> xs) => xs.ElementAt(1); }", false)]
    [InlineData("using System.Linq; class C { public int M() => new[] { 1, 2, 3 }.ElementAt(10); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new[] { 1, 2, 3 }.ElementAt(2); }", false)]
    [InlineData("using System.Linq; class C { public int M(int i) => new[] { 1, 2, 3 }.ElementAt(i); }", false)]
    public async Task NegativeElementAtIndexAnalyzer_Should_NotFlag_ANonnegativeIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeElementAtIndexAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeElementAtIndexAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M() => new string('x', -1); }", true)]
    [InlineData("class C { public string M() => new string('x', 5); }", false)]
    public async Task NegativeStringRepeatCountAnalyzer_Should_NotFlag_ANonnegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeStringRepeatCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeStringRepeatCountAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public int M(string s) => Convert.ToInt32(s, 3); }", true)]
    [InlineData("using System; class C { public int M(string s) => Convert.ToInt32(s, 16); }", false)]
    [InlineData("using System; class C { public int M(string s) => Convert.ToInt32(s, 10); }", false)]
    [InlineData("using System; class C { public byte M(string s) => Convert.ToByte(s, 3); }", true)]
    [InlineData("using System; class C { public byte M(string s) => Convert.ToByte(s, 16); }", false)]
    public async Task InvalidConvertToIntegralBaseAnalyzer_Should_NotFlag_AValidBase(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConvertToIntegralBaseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConvertToIntegralBaseAnalyzer.DiagnosticId));
    }

    // ── Ninety-seventh batch (двести сорок третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.IO; class C { public string M(string a) => Path.Combine(a, \"/etc/passwd\"); }", true)]
    [InlineData("using System.IO; class C { public string M(string a) => Path.Combine(a, \"C:\\\\Windows\"); }", true)]
    [InlineData("using System.IO; class C { public string M(string a) => Path.Combine(a, \"relative\", \"path\"); }", false)]
    [InlineData("using System.IO; class C { public string M(string a, string b) => Path.Combine(a, b); }", false)]
    public async Task AbsolutePathArgumentInPathCombineAnalyzer_Should_NotFlag_ARelativeOrNonLiteralArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new AbsolutePathArgumentInPathCombineAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == AbsolutePathArgumentInPathCombineAnalyzer.DiagnosticId));
    }

    // ── Ninety-ninth batch (двести сорок пятый проход) — one new analyzer plus a new PriorityQueue
    // form on an already-shipped one.

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) => xs.Chunk(0); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) => xs.Chunk(-1); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public void M(IEnumerable<int> xs) => xs.Chunk(5); }", false)]
    public async Task NonPositiveChunkSizeAnalyzer_Should_NotFlag_APositiveSize(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NonPositiveChunkSizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NonPositiveChunkSizeAnalyzer.DiagnosticId));
    }

    // ── Hundred and first batch (двести сорок седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int> { { \"a\", 1 }, { \"a\", 2 } }; }", true)]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int> { { \"a\", 1 }, { \"b\", 2 } }; }", false)]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int> { [\"a\"] = 1, [\"a\"] = 2 }; }", false)]
    [InlineData("using System; using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { \"a\", 1 }, { \"A\", 2 } }; }", true)]
    [InlineData("using System; using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { \"a\", 1 }, { \"b\", 2 } }; }", false)]
    [InlineData("using System.Collections.Generic; class C { public Dictionary<string, int> M() => new Dictionary<string, int> { { \"a\", 1 }, { \"A\", 2 } }; }", false)]
    public async Task DuplicateKeyInDictionaryInitializerAnalyzer_Should_NotFlag_DistinctKeysOrTheIndexerForm(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DuplicateKeyInDictionaryInitializerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DuplicateKeyInDictionaryInitializerAnalyzer.DiagnosticId));
    }

    // ── Hundred and second batch (двести сорок восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public HashSet<int> M() => new HashSet<int> { 1, 1 }; }", true)]
    [InlineData("using System.Collections.Generic; class C { public HashSet<int> M() => new HashSet<int> { 1, 2 }; }", false)]
    public async Task RedundantDuplicateValueInHashSetInitializerAnalyzer_Should_NotFlag_DistinctValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDuplicateValueInHashSetInitializerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDuplicateValueInHashSetInitializerAnalyzer.DiagnosticId));
    }

    // ── Hundred and fourth batch (двести пятидесятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int>(2) { 1, 2, 3 }; }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int>(3) { 1, 2, 3 }; }", false)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int>(5) { 1, 2, 3 }; }", false)]
    [InlineData("using System.Collections.Generic; class C { public HashSet<int> M() => new HashSet<int>(1) { 1, 2, 3 }; }", true)]
    public async Task InsufficientCollectionCapacityAnalyzer_Should_NotFlag_ASufficientCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InsufficientCollectionCapacityAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InsufficientCollectionCapacityAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifth batch (двести пятьдесят первый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm p) => Enum.IsDefined(typeof(Perm), p); }", true)]
    [InlineData("using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } class C { public bool M(Perm p) => Enum.IsDefined<Perm>(p); }", true)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public bool M(Color c) => Enum.IsDefined(typeof(Color), c); }", false)]
    public async Task IsDefinedOnFlagsEnumAnalyzer_Should_NotFlag_ANonFlagsEnum(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new IsDefinedOnFlagsEnumAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == IsDefinedOnFlagsEnumAnalyzer.DiagnosticId));
    }

    // ── Hundred and sixth batch (двести пятьдесят второй проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public byte[] M() => Convert.FromBase64String(\"abc\"); }", true)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromBase64String(\"abcd\"); }", false)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromBase64String(\"ab==\"); }", false)]
    [InlineData("using System; class C { public byte[] M(string s) => Convert.FromBase64String(s); }", false)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromBase64String(\"!!!!\"); }", true)]
    public async Task InvalidBase64LiteralLengthAnalyzer_Should_NotFlag_AMultipleOfFourOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBase64LiteralLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBase64LiteralLengthAnalyzer.DiagnosticId));
    }

    // ── Hundred and sixteenth batch (двести шестьдесят второй проход) — one new analyzer, plus
    // InvalidBase64LiteralLengthAnalyzer above rewritten to the "run the real validator" technique.

    [Theory]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"48656c6c6f\"); }", false)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"abc\"); }", true)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"zz\"); }", true)]
    [InlineData("using System; class C { public byte[] M(string s) => Convert.FromHexString(s); }", false)]
    public async Task InvalidHexStringLiteralAnalyzer_Should_NotFlag_AnEvenLengthHexLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHexStringLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHexStringLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventh batch (двести пятьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Guid M() => Guid.Parse(\"not-a-guid\"); }", true)]
    [InlineData("using System; class C { public Guid M() => Guid.Parse(\"12345678-1234-1234-1234-123456789abc\"); }", false)]
    [InlineData("using System; class C { public Guid M() => Guid.Parse(\"1234567812341234123412345678abcd\".Substring(0, 32)); }", false)]
    [InlineData("using System; class C { public Guid M(string s) => Guid.Parse(s); }", false)]
    public async Task InvalidGuidLiteralFormatAnalyzer_Should_NotFlag_AValidFormatOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidGuidLiteralFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidGuidLiteralFormatAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighth batch (двести пятьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Version M() => Version.Parse(\"abc\"); }", true)]
    [InlineData("using System; class C { public Version M() => Version.Parse(\"5\"); }", true)]
    [InlineData("using System; class C { public Version M() => Version.Parse(\"1.2.3.4.5\"); }", true)]
    [InlineData("using System; class C { public Version M() => Version.Parse(\"1.2\"); }", false)]
    [InlineData("using System; class C { public Version M() => Version.Parse(\"1.2.3.4\"); }", false)]
    [InlineData("using System; class C { public Version M(string s) => Version.Parse(s); }", false)]
    public async Task InvalidVersionLiteralFormatAnalyzer_Should_NotFlag_AValidFormatOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidVersionLiteralFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidVersionLiteralFormatAnalyzer.DiagnosticId));
    }

    // ── Hundred and ninth batch (двести пятьдесят пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfo(\"!!!invalid!!!\"); }", true)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfo(\"en-US\"); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => new CultureInfo(\"!!!invalid!!!\"); }", true)]
    [InlineData("using System.Globalization; class C { public CultureInfo M(string s) => CultureInfo.GetCultureInfo(s); }", false)]
    // Extended проход 440 (TODO #74, candidate #6 from the sixth research subagent survey) — GetCultureInfoByIetfLanguageTag.
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"###\"); }", true)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"\"); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"not-a-real-tag-zzz\"); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"en-US\"); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M(string x) => CultureInfo.GetCultureInfoByIetfLanguageTag(x); }", false)]
    [InlineData("class CultureInfo { public static CultureInfo GetCultureInfoByIetfLanguageTag(string s) => default; } class C { public void M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"###\"); }", false)]
    public async Task InvalidCultureNameLiteralAnalyzer_Should_NotFlag_ARecognizedCultureOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCultureNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCultureNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and tenth batch (двести пятьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"not-an-ip\"); }", true)]
    [InlineData("using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"999.999.999.999\"); }", true)]
    [InlineData("using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"192.168.1.1\"); }", false)]
    [InlineData("using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"::1\"); }", false)]
    [InlineData("using System.Net; class C { public IPAddress M(string s) => IPAddress.Parse(s); }", false)]
    public async Task InvalidIPAddressLiteralAnalyzer_Should_NotFlag_AValidAddressOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIPAddressLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIPAddressLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and eleventh batch (двести пятьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.Mail; class C { public MailAddress M() => new MailAddress(\"not-an-email\"); }", true)]
    [InlineData("using System.Net.Mail; class C { public MailAddress M() => new MailAddress(\"someone@example.com\"); }", false)]
    [InlineData("using System.Net.Mail; class C { public MailAddress M(string s) => new MailAddress(s); }", false)]
    public async Task InvalidMailAddressLiteralAnalyzer_Should_NotFlag_AValidAddressOrNonLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMailAddressLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMailAddressLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and twelfth batch (двести пятьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public int M() => Math.Abs(int.MinValue); }", true)]
    [InlineData("using System; class C { public int M() => Math.Abs(-2147483648); }", true)]
    [InlineData("using System; class C { public long M() => Math.Abs(long.MinValue); }", true)]
    [InlineData("using System; class C { public short M() => Math.Abs(short.MinValue); }", true)]
    [InlineData("using System; class C { public int M() => Math.Abs(-5); }", false)]
    [InlineData("using System; class C { public int M(int x) => Math.Abs(x); }", false)]
    public async Task MathAbsOfMinValueAnalyzer_Should_NotFlag_ANonMinValueArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new MathAbsOfMinValueAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == MathAbsOfMinValueAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirteenth batch (двести пятьдесят девятый проход) — one new analyzer plus a new
    // form on an already-shipped one.

    [Theory]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, string x) => sb.Insert(sb.Length, x); }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb, string x) => sb.Insert(0, x); }", false)]
    public async Task PreferAppendOverStringBuilderInsertAtLengthAnalyzer_Should_NotFlag_AnInsertAtZero(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new PreferAppendOverStringBuilderInsertAtLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == PreferAppendOverStringBuilderInsertAtLengthAnalyzer.DiagnosticId));
    }

    // ── Hundred and fourteenth batch (двухсотшестидесятый проход) — two new analyzers.

    [Theory]
    [InlineData("class C { public bool M() => bool.Parse(\"yes\"); }", true)]
    [InlineData("class C { public bool M() => bool.Parse(\"true\"); }", false)]
    [InlineData("class C { public bool M() => bool.Parse(\"  True  \"); }", false)]
    [InlineData("using System; class C { public bool M() => Convert.ToBoolean(\"nope\"); }", true)]
    [InlineData("using System; class C { public bool M() => Convert.ToBoolean(\"False\"); }", false)]
    [InlineData("class C { public bool M(string x) => bool.Parse(x); }", false)]
    public async Task InvalidBooleanLiteralAnalyzer_Should_NotFlag_AValidBooleanLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBooleanLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBooleanLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M() => Enum.Parse<Color>(\"Purple\"); }", true)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M() => Enum.Parse<Color>(\"Red\"); }", false)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M() => Enum.Parse<Color>(\"5\"); }", false)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public object M() => Enum.Parse(typeof(Color), \"Purple\"); }", true)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public object M() => Enum.Parse(typeof(Color), \"Red\"); }", false)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M() => Enum.Parse<Color>(\"Red, Green\"); }", false)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M() => Enum.Parse<Color>(\"Red, Purple\"); }", true)]
    [InlineData("using System; enum Color { Red, Green, Blue } class C { public Color M(string x) => Enum.Parse<Color>(x); }", false)]
    public async Task InvalidEnumParseLiteralAnalyzer_Should_NotFlag_ADefinedMemberName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEnumParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEnumParseLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public Uri M() => new Uri(\"http://\"); }", true)]
    [InlineData("using System; class C { public Uri M() => new Uri(\"http://example.com\"); }", false)]
    [InlineData("using System; class C { public Uri M() => new Uri(\"relative/path\"); }", false)]
    [InlineData("using System; class C { public Uri M() => new Uri(\"not a uri!!\", UriKind.Absolute); }", true)]
    [InlineData("using System; class C { public Uri M() => new Uri(\"http://example.com\", UriKind.Absolute); }", false)]
    [InlineData("using System; class C { public Uri M(string s) => new Uri(s); }", false)]
    [InlineData("using System; class C { public Uri M(Uri b) => new Uri(b, \"relative\"); }", false)]
    public async Task InvalidUriLiteralAnalyzer_Should_NotFlag_AValidUri(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidUriLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidUriLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventeenth batch (двести шестьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.Xml.Linq.XDocument M() => System.Xml.Linq.XDocument.Parse(\"not xml\"); }", true)]
    [InlineData("class C { public System.Xml.Linq.XDocument M() => System.Xml.Linq.XDocument.Parse(\"<a/>\"); }", false)]
    [InlineData("class C { public System.Xml.Linq.XElement M() => System.Xml.Linq.XElement.Parse(\"<a/><b/>\"); }", true)]
    [InlineData("class C { public System.Xml.Linq.XElement M() => System.Xml.Linq.XElement.Parse(\"<a>1</a>\"); }", false)]
    [InlineData("class C { public System.Xml.Linq.XDocument M(string s) => System.Xml.Linq.XDocument.Parse(s); }", false)]
    public async Task InvalidXmlLiteralAnalyzer_Should_NotFlag_AWellFormedXmlLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlLiteralAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighteenth batch (двести шестьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public byte M() => byte.Parse(\"300\"); }", true)]
    [InlineData("class C { public byte M() => byte.Parse(\"255\"); }", false)]
    [InlineData("class C { public sbyte M() => sbyte.Parse(\"-129\"); }", true)]
    [InlineData("class C { public int M() => int.Parse(\"99999999999\"); }", true)]
    [InlineData("class C { public int M() => int.Parse(\"2147483647\"); }", false)]
    [InlineData("using System; class C { public byte M() => Convert.ToByte(\"300\"); }", true)]
    [InlineData("class C { public int M() => int.Parse(\"12.5\"); }", false)]
    [InlineData("class C { public int M() => int.Parse(\"abc\"); }", false)]
    [InlineData("class C { public int M(string s) => int.Parse(s); }", false)]
    [InlineData("class C { public decimal M() => decimal.Parse(\"79228162514264337593543950336\"); }", true)]
    [InlineData("class C { public decimal M() => decimal.Parse(\"79228162514264337593543950335\"); }", false)]
    [InlineData("using System; class C { public decimal M() => Convert.ToDecimal(\"79228162514264337593543950336\"); }", true)]
    public async Task IntegralParseOverflowAnalyzer_Should_NotFlag_AnInRangeLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new IntegralParseOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == IntegralParseOverflowAnalyzer.DiagnosticId));
    }

    // ── Hundred and nineteenth batch (двести шестьдесят пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(double.NaN); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromHours(double.PositiveInfinity); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromSeconds(1e20); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(1); }", false)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(0); }", false)]
    [InlineData("using System; class C { public TimeSpan M(double x) => TimeSpan.FromDays(x); }", false)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromMicroseconds(double.NaN); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromMicroseconds(double.MaxValue); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromMicroseconds(1); }", false)]
    public async Task InvalidTimeSpanFromDoubleArgumentAnalyzer_Should_NotFlag_AnInRangeValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimeSpanFromDoubleArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimeSpanFromDoubleArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-first batch (двести шестьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M() { new System.IO.MemoryStream(); } }", true)]
    [InlineData("class C { public void M() { using var s = new System.IO.MemoryStream(); } }", false)]
    [InlineData("class C { public void M() { var s = new System.IO.MemoryStream(); s.Dispose(); } }", false)]
    [InlineData("class C { public System.IO.MemoryStream M() { return new System.IO.MemoryStream(); } }", false)]
    [InlineData("class C { public void M() { new object(); } }", false)]
    [InlineData("class C { public void M() { new System.IO.MemoryStream().Dispose(); } }", false)]
    public async Task DiscardedDisposableObjectCreationAnalyzer_Should_NotFlag_AnAssignedOrChainedCreation(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DiscardedDisposableObjectCreationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DiscardedDisposableObjectCreationAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-second batch (двести шестьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("record Foo(int[] Data);", true)]
    [InlineData("record Bar(int Data);", false)]
    [InlineData("record Baz { public int[] Data { get; init; } = System.Array.Empty<int>(); }", true)]
    [InlineData("record struct Qux(int[] Data);", true)]
    [InlineData("class NotARecord { public int[] Data { get; init; } = System.Array.Empty<int>(); }", false)]
    public async Task RecordWithArrayMemberAnalyzer_Should_NotFlag_ANonArrayMember(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RecordWithArrayMemberAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RecordWithArrayMemberAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-third batch (двести шестьдесят девятый проход) — extension of an already-
    // shipped analyzer, no new analyzer.

    [Theory]
    [InlineData("record Foo(System.Collections.Generic.List<int> Data);", true)]
    [InlineData("record Bar(System.Collections.Generic.Dictionary<string, int> Data);", true)]
    [InlineData("record Baz(System.Collections.Generic.HashSet<int> Data);", true)]
    [InlineData("record Qux { public System.Collections.Generic.List<int> Data { get; init; } = new(); }", true)]
    [InlineData("record Quux(System.Collections.Generic.IReadOnlyList<int> Data);", false)]
    [InlineData("record Corge(System.Collections.Immutable.ImmutableArray<int> Data);", false)]
    public async Task RecordWithArrayMemberAnalyzer_Should_NotFlag_AnInterfaceOrImmutableCollectionMember(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RecordWithArrayMemberAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RecordWithArrayMemberAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-fourth batch (двести семидесятый проход) — one new analyzer.

    [Theory]
    [InlineData("#nullable disable\nclass Foo : System.IEquatable<Foo> { public int X; public bool Equals(Foo other) => other != null && X == other.X; public override int GetHashCode() => X; }", true)]
    [InlineData("#nullable disable\nclass Bar : System.IEquatable<Bar> { public int X; public bool Equals(Bar other) => other != null && X == other.X; public override bool Equals(object obj) => Equals(obj as Bar); public override int GetHashCode() => X; }", false)]
    [InlineData("struct Baz : System.IEquatable<Baz> { public int X; public readonly bool Equals(Baz other) => X == other.X; public override readonly int GetHashCode() => X; }", true)]
    [InlineData("class Quux { public int X; public override bool Equals(object obj) => obj is Quux q && X == q.X; public override int GetHashCode() => X; }", false)]
    [InlineData("record Corge(int X);", false)]
    public async Task IEquatableWithoutObjectEqualsOverrideAnalyzer_Should_NotFlag_ATypeWithBothOverrides(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new IEquatableWithoutObjectEqualsOverrideAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == IEquatableWithoutObjectEqualsOverrideAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-fifth batch (двести семьдесят первый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M(int[] arr) => System.Array.Resize(ref arr, -1); }", true)]
    [InlineData("class C { public void M(int[] arr) => System.Array.Resize(ref arr, 5); }", false)]
    [InlineData("class C { public void M(int[] arr) => System.Array.Clear(arr, -1, 1); }", true)]
    [InlineData("class C { public void M(int[] arr) => System.Array.Clear(arr, 0, -1); }", true)]
    [InlineData("class C { public void M(int[] arr) => System.Array.Clear(arr, 0, 1); }", false)]
    [InlineData("class C { public void M(int[] arr, int n) => System.Array.Resize(ref arr, n); }", false)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.Copy(src, -1, dst, 0, 1); }", true)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.Copy(src, 0, dst, -1, 1); }", true)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.Copy(src, 0, dst, 0, -1); }", true)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.Copy(src, 1, dst, 1, 2); }", false)]
    [InlineData("class C { public void M(int[] src, int[] dst, int n) => System.Array.Copy(src, n, dst, 0, 1); }", false)]
    [InlineData("class C { public int M(int[] arr) => System.Array.BinarySearch(arr, -1, 1, 2); }", true)]
    [InlineData("class C { public int M(int[] arr) => System.Array.BinarySearch(arr, 0, -1, 2); }", true)]
    [InlineData("class C { public int M(int[] arr) => System.Array.BinarySearch(arr, 0, 1, 2); }", false)]
    [InlineData("class C { public int M(int[] arr) => System.Array.BinarySearch(arr, 2); }", false)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.ConstrainedCopy(src, -1, dst, 0, 1); }", true)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.ConstrainedCopy(src, 0, dst, -1, 1); }", true)]
    [InlineData("class C { public void M(int[] src, int[] dst) => System.Array.ConstrainedCopy(src, 1, dst, 1, 2); }", false)]
    [InlineData("class C { public void M(int[] a) => System.Array.Reverse(a, -1, 2); }", true)]
    [InlineData("class C { public void M(int[] a) => System.Array.Reverse(a, 0, -1); }", true)]
    [InlineData("class C { public void M(int[] a) => System.Array.Sort(a, -1, 2); }", true)]
    [InlineData("class C { public void M(int[] a) => System.Array.Sort(a, 0, -1); }", true)]
    [InlineData("class C { public void M(int[] a) => System.Array.Reverse(a, 0, 4); }", false)]
    [InlineData("class C { public void M(int[] a) => System.Array.Sort(a, 0, 4); }", false)]
    public async Task NegativeArrayResizeOrClearArgumentAnalyzer_Should_NotFlag_ANonNegativeArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeArrayResizeOrClearArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeArrayResizeOrClearArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and twenty-eighth batch (двести семьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 13, 1); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 1, 32); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 2, 29); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2024, 2, 29); }", false)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 1, 1, 24, 0, 0); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 1, 1); }", false)]
    [InlineData("using System; class C { public DateTime M(int m) => new DateTime(2023, m, 1); }", false)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc); }", false)]
    [InlineData("using System; class C { public DateOnly M() => new DateOnly(2023, 13, 1); }", true)]
    [InlineData("using System; class C { public DateOnly M() => new DateOnly(2023, 1, 1); }", false)]
    [InlineData("using System; class C { public TimeOnly M() => new TimeOnly(24, 0, 0); }", true)]
    [InlineData("using System; class C { public TimeOnly M() => new TimeOnly(1, 60); }", true)]
    [InlineData("using System; class C { public TimeOnly M() => new TimeOnly(1, 2, 3); }", false)]
    [InlineData("using System; class C { public TimeOnly M() => new TimeOnly(25, 0, 0, 0, 0); }", true)]
    [InlineData("using System; class C { public TimeOnly M() => new TimeOnly(1, 0, 0, 0, 0); }", false)]
    public async Task InvalidDateTimeConstructorArgumentAnalyzer_Should_NotFlag_AValidDate(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateTimeConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateTimeConstructorArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirtieth batch (двести семьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Version M() => new Version(-1, 0); }", true)]
    [InlineData("using System; class C { public Version M() => new Version(1, -1); }", true)]
    [InlineData("using System; class C { public Version M() => new Version(1, 0); }", false)]
    [InlineData("using System; class C { public Version M() => new Version(1, 0, -1); }", true)]
    [InlineData("using System; class C { public Version M(int m) => new Version(m, 0); }", false)]
    public async Task NegativeVersionComponentAnalyzer_Should_NotFlag_ANonNegativeComponent(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeVersionComponentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeVersionComponentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-first batch (двести семьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Guid M() => new Guid(new byte[] { 1, 2, 3 }); }", true)]
    [InlineData("using System; class C { public Guid M() => new Guid(new byte[16]); }", false)]
    [InlineData("using System; class C { public Guid M() => new Guid(1, 2, 3, new byte[] { 1, 2, 3 }); }", true)]
    [InlineData("using System; class C { public Guid M() => new Guid(1, 2, 3, new byte[8]); }", false)]
    [InlineData("using System; class C { public Guid M(byte[] b) => new Guid(b); }", false)]
    public async Task InvalidGuidByteArrayLengthAnalyzer_Should_NotFlag_ACorrectlySizedArray(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidGuidByteArrayLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidGuidByteArrayLengthAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-second batch (двести семьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net; class C { public IPAddress M() => new IPAddress(new byte[] { 1, 2, 3 }); }", true)]
    [InlineData("using System.Net; class C { public IPAddress M() => new IPAddress(new byte[] { 1, 2, 3, 4 }); }", false)]
    [InlineData("using System.Net; class C { public IPAddress M() => new IPAddress(new byte[16]); }", false)]
    [InlineData("using System.Net; class C { public IPAddress M(byte[] b) => new IPAddress(b); }", false)]
    public async Task InvalidIPAddressByteArrayLengthAnalyzer_Should_NotFlag_ACorrectlySizedArray(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIPAddressByteArrayLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIPAddressByteArrayLengthAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-third batch (двести семьдесят девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public int M() => BitConverter.ToInt32(new byte[4], -1); }", true)]
    [InlineData("using System; class C { public int M() => BitConverter.ToInt32(new byte[4], 1); }", true)]
    [InlineData("using System; class C { public int M() => BitConverter.ToInt32(new byte[4], 0); }", false)]
    [InlineData("using System; class C { public short M() => BitConverter.ToInt16(new byte[1], 0); }", true)]
    [InlineData("using System; class C { public int M(int i) => BitConverter.ToInt32(new byte[4], i); }", false)]
    [InlineData("using System; class C { public string M() => BitConverter.ToString(new byte[4], 4, 0); }", true)]
    [InlineData("using System; class C { public string M() => BitConverter.ToString(new byte[4], 2, 3); }", true)]
    [InlineData("using System; class C { public string M() => BitConverter.ToString(new byte[4], 0, -1); }", true)]
    [InlineData("using System; class C { public string M() => BitConverter.ToString(new byte[4], 2, 2); }", false)]
    [InlineData("using System; class C { public string M() => BitConverter.ToString(new byte[0], 0, 0); }", false)]
    public async Task InvalidBitConverterArgumentAnalyzer_Should_NotFlag_ASufficientArray(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBitConverterArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBitConverterArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-fourth batch (двухсотвосьмидесятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M() => new string(new char[3], 0, 5); }", true)]
    [InlineData("class C { public string M() => new string(new char[3], -1, 1); }", true)]
    [InlineData("class C { public string M() => new string(new char[3], 2, 2); }", true)]
    [InlineData("class C { public string M() => new string(new char[3], 1, 2); }", false)]
    [InlineData("class C { public string M() => new string(new char[3], 3, 0); }", false)]
    [InlineData("class C { public string M(int s, int l) => new string(new char[3], s, l); }", false)]
    public async Task InvalidStringCharArrayConstructorArgumentAnalyzer_Should_NotFlag_AnInBoundsRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringCharArrayConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringCharArrayConstructorArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-fifth batch (двести восемьдесят первый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M() => System.Text.Encoding.UTF8.GetString(new byte[3], 0, 5); }", true)]
    [InlineData("class C { public string M() => System.Text.Encoding.UTF8.GetString(new byte[3], -1, 1); }", true)]
    [InlineData("class C { public string M() => System.Text.Encoding.UTF8.GetString(new byte[3], 1, 2); }", false)]
    [InlineData("class C { public string M() => System.Text.Encoding.UTF8.GetString(new byte[3], 3, 0); }", false)]
    [InlineData("class C { public string M(int i, int c) => System.Text.Encoding.UTF8.GetString(new byte[3], i, c); }", false)]
    public async Task InvalidEncodingGetStringArgumentAnalyzer_Should_NotFlag_AnInBoundsRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEncodingGetStringArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEncodingGetStringArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-seventh batch (двести восемьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Span<byte> M() => new byte[3].AsSpan(1, 5); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new byte[3].AsSpan(-1, 1); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new byte[3].AsSpan(1, 2); }", false)]
    [InlineData("using System; class C { public Memory<byte> M() => new byte[3].AsMemory(1, 5); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new byte[3].AsSpan(4); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new byte[3].AsSpan(1); }", false)]
    [InlineData("using System; class C { public Span<byte> M(int s, int l) => new byte[3].AsSpan(s, l); }", false)]
    public async Task InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer_Should_NotFlag_AnInBoundsRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-eighth batch (двести восемьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public string M() => Convert.ToBase64String(new byte[3], 0, 5); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToBase64String(new byte[3], -1, 1); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToBase64String(new byte[3], 1, 2); }", false)]
    [InlineData("using System; class C { public string M(int o, int l) => Convert.ToBase64String(new byte[3], o, l); }", false)]
    [InlineData("using System; class C { public string M() => Convert.ToHexString(new byte[4], -1, 2); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToHexString(new byte[4], 0, -1); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToHexString(new byte[4], 2, 3); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToHexString(new byte[4], 0, 4); }", false)]
    [InlineData("using System; class C { public string M() => Convert.ToHexStringLower(new byte[4], -1, 2); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToHexStringLower(new byte[4], 2, 3); }", true)]
    [InlineData("using System; class C { public string M() => Convert.ToHexStringLower(new byte[4], 0, 4); }", false)]
    public async Task InvalidConvertToBase64StringArgumentAnalyzer_Should_NotFlag_AnInBoundsRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConvertToBase64StringArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConvertToBase64StringArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and thirty-ninth batch (двести восемьдесят пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(5, 3); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(1, 0); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(3, 5); }", false)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(-1, 10); }", false)]
    [InlineData("using System.Text; class C { public StringBuilder M(int c, int m) => new StringBuilder(c, m); }", false)]
    public async Task InvalidStringBuilderMaxCapacityArgumentAnalyzer_Should_NotFlag_AValidCapacityPair(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringBuilderMaxCapacityArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringBuilderMaxCapacityArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and fortieth batch (двести восемьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.ECMAScript | RegexOptions.RightToLeft); }", true)]
    [InlineData("using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.ECMAScript | RegexOptions.IgnoreCase); }", false)]
    [InlineData("using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.IgnoreCase); }", false)]
    [InlineData("using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.NonBacktracking | RegexOptions.RightToLeft); }", true)]
    [InlineData("using System.Text.RegularExpressions; class C { public Regex M(RegexOptions o) => new Regex(\"a\", o); }", false)]
    public async Task InvalidRegexOptionsCombinationAnalyzer_Should_NotFlag_AValidCombination(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRegexOptionsCombinationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRegexOptionsCombinationAnalyzer.DiagnosticId));
    }

    // ── Hundred and forty-first batch (двести восемьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public int M() => int.Parse(\"1\", NumberStyles.AllowHexSpecifier | NumberStyles.AllowDecimalPoint); }", true)]
    [InlineData("using System.Globalization; class C { public int M() => int.Parse(\"1\", NumberStyles.AllowHexSpecifier); }", false)]
    [InlineData("using System.Globalization; class C { public decimal M() => decimal.Parse(\"1\", NumberStyles.AllowHexSpecifier); }", true)]
    [InlineData("using System.Globalization; class C { public int M() => int.Parse(\"1\", NumberStyles.Integer); }", false)]
    [InlineData("using System.Globalization; class C { public int M(NumberStyles s) => int.Parse(\"1\", s); }", false)]
    [InlineData("using System.Globalization; using System.Numerics; class C { public BigInteger M(string s) => BigInteger.Parse(s, NumberStyles.AllowHexSpecifier | NumberStyles.AllowDecimalPoint); }", true)]
    [InlineData("using System.Globalization; using System.Numerics; class C { public BigInteger M(string s) => BigInteger.Parse(s, NumberStyles.AllowHexSpecifier); }", false)]
    public async Task InvalidNumberStylesCombinationAnalyzer_Should_NotFlag_AValidCombination(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidNumberStylesCombinationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidNumberStylesCombinationAnalyzer.DiagnosticId));
    }

    // ── Hundred and forty-second batch (двести восемьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AssumeUniversal); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.RoundtripKind); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string s, DateTimeStyles st) => DateTime.Parse(s, CultureInfo.InvariantCulture, st); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTimeOffset M(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AssumeUniversal); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTimeOffset M(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal); }", false)]
    public async Task InvalidDateTimeStylesCombinationAnalyzer_Should_NotFlag_AValidCombination(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateTimeStylesCombinationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateTimeStylesCombinationAnalyzer.DiagnosticId));
    }

    // ── Hundred and forty-third batch (двести восемьдесят девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public int M(string a, string b) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.Ordinal | CompareOptions.IgnoreCase); }", true)]
    [InlineData("using System.Globalization; class C { public int M(string a, string b) => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.OrdinalIgnoreCase | CompareOptions.IgnoreSymbols); }", true)]
    [InlineData("using System.Globalization; class C { public int M(string a, string b) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.Ordinal); }", false)]
    [InlineData("using System.Globalization; class C { public int M(string a, string b) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols); }", false)]
    [InlineData("using System.Globalization; class C { public int M(string a, string b, CompareOptions o) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, o); }", false)]
    public async Task InvalidCompareOptionsCombinationAnalyzer_Should_NotFlag_AValidCombination(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCompareOptionsCombinationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCompareOptionsCombinationAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task PreferInstanceToStringOverConvertToStringForNumericAnalyzer_Should_NotFlag_AConvertToStringBaseCall()
    {
        const string code = "class C { public string M(int x) => System.Convert.ToString(x, 3); }";
        var diagnostics = await GetDiagnosticsAsync(new PreferInstanceToStringOverConvertToStringForNumericAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == PreferInstanceToStringOverConvertToStringForNumericAnalyzer.DiagnosticId);
    }

    // ── Hundred and forty-fifth batch (двести девяносто первый проход) — one new analyzer, plus a
    // false-positive fix to PreferInstanceToStringOverConvertToStringForNumericAnalyzer/RPAD0220 found
    // while brainstorming it (see the [Fact] above).

    [Theory]
    [InlineData("class C { public string M(int x) => System.Convert.ToString(x, 3); }", true)]
    [InlineData("class C { public string M(int x) => System.Convert.ToString(x, 16); }", false)]
    [InlineData("class C { public string M(long x) => System.Convert.ToString(x, 3); }", true)]
    [InlineData("class C { public string M(int x, int b) => System.Convert.ToString(x, b); }", false)]
    public async Task InvalidConvertToStringBaseAnalyzer_Should_NotFlag_AValidBase(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConvertToStringBaseAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConvertToStringBaseAnalyzer.DiagnosticId));
    }

    // ── Hundred and forty-eighth batch (двести девяносто четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.None, TimeSpan.Zero); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.None, TimeSpan.FromDays(25)); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.None, TimeSpan.FromSeconds(1)); }", false)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Regex M() => new Regex(\"a\", RegexOptions.None, Regex.InfiniteMatchTimeout); }", false)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Regex M(TimeSpan t) => new Regex(\"a\", RegexOptions.None, t); }", false)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public bool M() => Regex.IsMatch(\"a\", \"a\", RegexOptions.None, TimeSpan.Zero); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public Match M() => Regex.Match(\"a\", \"a\", RegexOptions.None, TimeSpan.Zero); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public string M() => Regex.Replace(\"a\", \"a\", \"b\", RegexOptions.None, TimeSpan.Zero); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public string[] M() => Regex.Split(\"a\", \"a\", RegexOptions.None, TimeSpan.Zero); }", true)]
    [InlineData("using System; using System.Text.RegularExpressions; class C { public bool M() => Regex.IsMatch(\"a\", \"a\", RegexOptions.None, TimeSpan.FromSeconds(1)); }", false)]
    public async Task InvalidRegexMatchTimeoutAnalyzer_Should_NotFlag_AValidTimeout(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRegexMatchTimeoutAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRegexMatchTimeoutAnalyzer.DiagnosticId));
    }

    // ── Hundred and fiftieth batch (двести девяносто шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; using System.Threading; class C { public CancellationTokenSource M() => new CancellationTokenSource(TimeSpan.FromSeconds(-5)); }", true)]
    [InlineData("using System; using System.Threading; class C { public CancellationTokenSource M() => new CancellationTokenSource(Timeout.InfiniteTimeSpan); }", false)]
    [InlineData("using System; using System.Threading; class C { public CancellationTokenSource M() => new CancellationTokenSource(TimeSpan.Zero); }", false)]
    [InlineData("using System; using System.Threading; class C { public CancellationTokenSource M() => new CancellationTokenSource(TimeSpan.FromSeconds(5)); }", false)]
    [InlineData("using System; using System.Threading; class C { public CancellationTokenSource M(TimeSpan t) => new CancellationTokenSource(t); }", false)]
    [InlineData("using System; using System.Threading.Tasks; class C { public Task M() => Task.Delay(TimeSpan.FromSeconds(-5)); }", true)]
    [InlineData("using System; using System.Threading; using System.Threading.Tasks; class C { public Task M() => Task.Delay(Timeout.InfiniteTimeSpan); }", false)]
    [InlineData("using System; using System.Threading.Tasks; class C { public Task M() => Task.Delay(TimeSpan.Zero); }", false)]
    [InlineData("using System; using System.Threading.Tasks; class C { public Task M() => Task.Delay(TimeSpan.FromSeconds(5)); }", false)]
    public async Task InvalidCancellationTokenSourceTimeSpanDelayAnalyzer_Should_NotFlag_AValidDelay(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCancellationTokenSourceTimeSpanDelayAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCancellationTokenSourceTimeSpanDelayAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-second batch (двести девяносто восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M() => \"hello\".Remove(1, 10); }", true)]
    [InlineData("class C { public string M() => \"hello\".Remove(1, 2); }", false)]
    [InlineData("class C { public string M() => \"hello\".Insert(6, \"x\"); }", true)]
    [InlineData("class C { public string M() => \"hello\".Insert(-1, \"x\"); }", true)]
    [InlineData("class C { public string M() => \"hello\".Insert(0, \"x\"); }", false)]
    [InlineData("class C { public string M() => \"hello\".Insert(5, \"x\"); }", false)]
    [InlineData("class C { public string M(string s) => s.Remove(1, 10); }", false)]
    public async Task InvalidStringRemoveOrInsertArgumentAnalyzer_Should_NotFlag_AnInRangeCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringRemoveOrInsertArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringRemoveOrInsertArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-third batch (двести девяносто девятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public char M() => \"hello\"[10]; }", true)]
    [InlineData("class C { public char M() => \"hello\"[-1]; }", true)]
    [InlineData("class C { public char M() => \"hello\"[4]; }", false)]
    [InlineData("class C { public char M() => \"hello\"[5]; }", true)]
    [InlineData("class C { public char M(int i) => \"hello\"[i]; }", false)]
    [InlineData("class C { public char M(string s) => s[10]; }", false)]
    [InlineData("class C { public char M() => \"hello\"[^10]; }", true)]
    [InlineData("class C { public char M() => \"hello\"[^1]; }", false)]
    [InlineData("class C { public char M() => \"hello\"[^0]; }", true)]
    public async Task StringLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInRangeIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new StringLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == StringLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-fourth batch (трёхсотый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[10]; }", true)]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[2]; }", false)]
    [InlineData("class C { public int M() => new int[] { 1, 2, 3 }[10]; }", true)]
    [InlineData("class C { public int M(int i) => new[] { 1, 2, 3 }[i]; }", false)]
    [InlineData("class C { public int M(int[] xs) => xs[10]; }", false)]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[^5]; }", true)]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[^1]; }", false)]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[^0]; }", true)]
    public async Task ArrayLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInRangeIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ArrayLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ArrayLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-fifth batch (триста первый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M() => Enumerable.Range(int.MaxValue, 2); }", true)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M() => Enumerable.Range(2000000000, 200000000); }", true)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M() => Enumerable.Range(int.MaxValue, 1); }", false)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M() => Enumerable.Range(0, 5); }", false)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M(int s, int c) => Enumerable.Range(s, c); }", false)]
    public async Task EnumerableRangeOverflowAnalyzer_Should_NotFlag_ANonOverflowingRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EnumerableRangeOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EnumerableRangeOverflowAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-seventh batch (триста третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public char M() => Convert.ToChar(-1); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(65536); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(65); }", false)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(65535); }", false)]
    [InlineData("using System; class C { public char M(int i) => Convert.ToChar(i); }", false)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((sbyte)-1); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((uint)65536); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((ulong)65536); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((sbyte)65); }", false)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((byte)255); }", false)]
    [InlineData("using System; class C { public char M() => Convert.ToChar((ushort)65535); }", false)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(70000.0); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(-1.0f); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(70000m); }", true)]
    [InlineData("using System; class C { public char M() => Convert.ToChar(65.0); }", false)]
    public async Task ConvertToCharOverflowAnalyzer_Should_NotFlag_AnInRangeValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ConvertToCharOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ConvertToCharOverflowAnalyzer.DiagnosticId));
    }

    // ── Hundred and fifty-ninth batch (триста пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public byte M() => Convert.ToByte(300); }", true)]
    [InlineData("using System; class C { public byte M() => Convert.ToByte(-1); }", true)]
    [InlineData("using System; class C { public byte M() => Convert.ToByte(255); }", false)]
    [InlineData("using System; class C { public sbyte M() => Convert.ToSByte(200); }", true)]
    [InlineData("using System; class C { public short M() => Convert.ToInt16(40000); }", true)]
    [InlineData("using System; class C { public ushort M() => Convert.ToUInt16(-1); }", true)]
    [InlineData("using System; class C { public byte M(int i) => Convert.ToByte(i); }", false)]
    [InlineData("using System; class C { public byte M() => Convert.ToByte(300L); }", true)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(5000000000L); }", true)]
    [InlineData("using System; class C { public uint M() => Convert.ToUInt32(-1L); }", true)]
    [InlineData("using System; class C { public uint M() => Convert.ToUInt32(-1); }", true)]
    [InlineData("using System; class C { public ulong M() => Convert.ToUInt64(-1L); }", true)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(100L); }", false)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(1e20); }", true)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(1e20m); }", true)]
    [InlineData("using System; class C { public long M() => Convert.ToInt64(1e20); }", true)]
    [InlineData("using System; class C { public ulong M() => Convert.ToUInt64(1.5e19); }", false)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(1.5); }", false)]
    [InlineData("using System; class C { public decimal M() => Convert.ToDecimal(double.MaxValue); }", true)]
    [InlineData("using System; class C { public decimal M() => Convert.ToDecimal(100.5); }", false)]
    public async Task ConvertNumericNarrowingOverflowAnalyzer_Should_NotFlag_AnInRangeValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ConvertNumericNarrowingOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ConvertNumericNarrowingOverflowAnalyzer.DiagnosticId));
    }

    // ── Hundred and sixty-third batch (триста девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Sort(-1, 2, null); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Sort(0, -1, null); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Reverse(-1, 2); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Reverse(0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.GetRange(-1, 2); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.RemoveRange(0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Sort(0, 4, null); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int i, int c) => xs.Sort(i, c, null); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.BinarySearch(-1, 2, 1, null); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.BinarySearch(0, -1, 1, null); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int[] a) => xs.CopyTo(-1, a, 0, 2); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int[] a) => xs.CopyTo(0, a, -1, 2); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int[] a) => xs.CopyTo(0, a, 0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int[] a) => xs.CopyTo(0, a, 0, 4); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.BinarySearch(0, 4, 1, null); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.IndexOf(0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.LastIndexOf(0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.IndexOf(0, 0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.LastIndexOf(0, 2, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.IndexOf(0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.IndexOf(0, 0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs) => xs.IndexOf(0, 0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> xs, int n) => xs.IndexOf(0, -n); }", false)]
    public async Task NegativeListIndexOrCountArgumentAnalyzer_Should_NotFlag_ANonNegativeArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeListIndexOrCountArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeListIndexOrCountArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and sixty-fifth batch (триста одиннадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(\"hello\").Remove(10, 1); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(\"hello\").Remove(1, 10); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(\"hello\").Insert(10, \"x\"); }", true)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(\"hello\").Remove(1, 2); }", false)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(\"hello\").Insert(5, \"x\"); }", false)]
    [InlineData("using System.Text; class C { public StringBuilder M() => new StringBuilder(5).Remove(0, 1); }", false)]
    [InlineData("using System.Text; class C { public StringBuilder M(StringBuilder sb) => sb.Remove(10, 1); }", false)]
    public async Task InvalidStringBuilderRemoveOrInsertArgumentAnalyzer_Should_NotFlag_AnInRangeCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringBuilderRemoveOrInsertArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringBuilderRemoveOrInsertArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and sixty-sixth batch (триста двенадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public Guid M(string s) => Guid.ParseExact(s, \"Q\"); }", true)]
    [InlineData("using System; class C { public Guid M(string s) => Guid.ParseExact(s, \"\"); }", true)]
    [InlineData("using System; class C { public Guid M(string s) => Guid.ParseExact(s, \"N\"); }", false)]
    [InlineData("using System; class C { public Guid M(string s) => Guid.ParseExact(s, \"n\"); }", false)]
    [InlineData("using System; class C { public Guid M(string s, string fmt) => Guid.ParseExact(s, fmt); }", false)]
    public async Task InvalidGuidParseExactFormatAnalyzer_Should_NotFlag_AValidFormat(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidGuidParseExactFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidGuidParseExactFormatAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventy-first batch (триста семнадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Linq; class C { public int M() => new int[0].First(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Single(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Last(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].First(x => x > 0); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new[] { 1 }.First(); }", false)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.First(); }", false)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Aggregate((a, b) => a + b); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Aggregate(0, (a, b) => a + b); }", false)]
    [InlineData("using System.Linq; class C { public int M() => new[] { 1, 2 }.Aggregate((a, b) => a + b); }", false)]
    [InlineData("using System.Linq; class C { public double M() => new int[0].Average(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Min(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].Max(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].MinBy(v => v); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new int[0].MaxBy(v => v); }", true)]
    [InlineData("using System.Linq; class C { public double? M() => new double?[0].Average(); }", false)]
    [InlineData("using System.Linq; class C { public int? M() => new int?[0].Min(); }", false)]
    [InlineData("using System.Linq; class C { public string M() => new string[0].Max()!; }", false)]
    [InlineData("using System.Linq; class C { public string M() => new string[0].MinBy(v => v)!; }", false)]
    [InlineData("using System.Linq; class C { public double M() => new string[0].Average(s => 1.0); }", true)]
    [InlineData("using System.Linq; class C { public double M() => new int[] { 1 }.Average(); }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public int M() => new int[0].Min(Comparer<int>.Default); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableArray<int>.Empty.First(); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableList<int>.Empty.Single(); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableHashSet<int>.Empty.Last(); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableSortedSet<int>.Empty.Aggregate((a, b) => a + b); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableStack<int>.Empty.First(); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableQueue<int>.Empty.First(); }", true)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableArray.Create(1).First(); }", false)]
    [InlineData("using System.Linq; using System.Collections.Immutable; class C { public int M() => ImmutableArray<int>.Empty.Aggregate((a, b) => a + b); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray<int>.Empty; }", false)]
    public async Task EmptyArrayLiteralFirstOrSingleOrLastAnalyzer_Should_NotFlag_ANonemptyReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EmptyArrayLiteralFirstOrSingleOrLastAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EmptyArrayLiteralFirstOrSingleOrLastAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventy-third batch (триста девятнадцатый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M() => new Queue<int>().Dequeue(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new Queue<int>().Peek(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new Stack<int>().Pop(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new Stack<int>().Peek(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new Queue<int>(10).Dequeue(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(Queue<int> q) => q.Dequeue(); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new PriorityQueue<int, int>().Dequeue(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new PriorityQueue<int, int>().Peek(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new LinkedList<int>().RemoveFirst(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new LinkedList<int>().RemoveLast(); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M(PriorityQueue<int, int> pq) => pq.Dequeue(); }", false)]
    public async Task EmptyQueueOrStackDequeueOrPopAnalyzer_Should_NotFlag_ANonLiteralReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EmptyQueueOrStackDequeueOrPopAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EmptyQueueOrStackDequeueOrPopAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventy-fifth batch (триста двадцать первый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M(string s) => s.PadLeft(-1); }", true)]
    [InlineData("class C { public string M(string s) => s.PadRight(-1); }", true)]
    [InlineData("class C { public string M(string s) => s.PadLeft(-1, 'x'); }", true)]
    [InlineData("class C { public string M(string s) => s.PadLeft(5); }", false)]
    [InlineData("class C { public string M(string s, int n) => s.PadLeft(n); }", false)]
    public async Task NegativePadWidthArgumentAnalyzer_Should_NotFlag_ANonNegativeWidth(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativePadWidthArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativePadWidthArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventy-seventh batch (триста двадцать третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Capacity = -1; }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Capacity = 5; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int n) => xs.Capacity = -n; }", false)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.Length = -1; }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.Length = 5; }", false)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.Position = -1; }", true)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.Position = 5; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Capacity = 1; }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Capacity = 3; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Capacity = 5; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.Capacity = 1; }", false)]
    [InlineData("using System.IO; class C { public void M() { new MemoryStream().Capacity = -1; } }", true)]
    [InlineData("using System.IO; class C { public void M() { new MemoryStream(new byte[10]).Capacity = -1; } }", true)]
    [InlineData("using System.IO; class C { public void M() { new MemoryStream().Capacity = 100; } }", false)]
    public async Task NegativeListCapacityAssignmentAnalyzer_Should_NotFlag_ANonNegativeCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeListCapacityAssignmentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeListCapacityAssignmentAnalyzer.DiagnosticId));
    }

    // ── Hundred and seventy-ninth batch (триста двадцать пятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0, -1, 1); }", true)]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0, 0, -1); }", true)]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0, 2, 5); }", true)]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0, 3, 0); }", false)]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0, 0, 3); }", false)]
    [InlineData("class C { public void M() => System.Array.Fill(new int[3], 0); }", false)]
    [InlineData("class C { public void M(int start, int count) => System.Array.Fill(new int[3], 0, start, count); }", false)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, -1, 1); }", true)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, 0, -1); }", true)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, 2, 5); }", true)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, 3, 0); }", false)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, 0, 3); }", false)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0); }", false)]
    [InlineData("class C { public void M() => System.Array.IndexOf(new int[3], 0, 0); }", false)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, -1, 1); }", false)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], -1, 1, x => true); }", true)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], 0, -1, x => true); }", true)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], 2, 5, x => true); }", true)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], 3, 0, x => true); }", false)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], 0, 3, x => true); }", false)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], x => true); }", false)]
    [InlineData("class C { public void M() => System.Array.FindIndex(new int[3], 0, x => true); }", false)]
    public async Task InvalidArrayFillArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayFillArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayFillArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighty-fourth batch (триста тридцатый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, -1, 1); }", true)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, 2, -1); }", true)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, 3, 1); }", true)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, 2, 4); }", true)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, 2, 3); }", false)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[3], 0, 1, 0); }", false)]
    [InlineData("class C { public void M() => System.Array.FindLastIndex(new int[3], 3, 1, x => true); }", true)]
    [InlineData("class C { public void M() => System.Array.FindLastIndex(new int[3], 2, 3, x => true); }", false)]
    [InlineData("class C { public void M() => System.Array.FindLastIndex(new int[3], x => true); }", false)]
    [InlineData("class C { public void M() => System.Array.LastIndexOf(new int[0], 0, -1, 0); }", false)]
    public async Task InvalidArrayLastIndexOfArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayLastIndexOfArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayLastIndexOfArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and eightieth batch (триста двадцать шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Buffers; class C { public void M() => ArrayPool<int>.Shared.Rent(-1); }", true)]
    [InlineData("using System.Buffers; class C { public void M() => ArrayPool<int>.Shared.Rent(0); }", false)]
    [InlineData("using System.Buffers; class C { public void M() => ArrayPool<int>.Shared.Rent(5); }", false)]
    [InlineData("using System.Buffers; class C { public void M(int n) => ArrayPool<int>.Shared.Rent(-n); }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var p = ArrayPool<int>.Create(); p.Rent(-1); } }", true)]
    public async Task NegativeArrayPoolRentLengthAnalyzer_Should_NotFlag_ANonNegativeLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeArrayPoolRentLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeArrayPoolRentLengthAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighty-first batch (триста двадцать седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M() => System.Console.SetCursorPosition(-1, 0); }", true)]
    [InlineData("class C { public void M() => System.Console.SetCursorPosition(0, -1); }", true)]
    [InlineData("class C { public void M() => System.Console.SetCursorPosition(-1, -1); }", true)]
    [InlineData("class C { public void M() => System.Console.SetCursorPosition(0, 0); }", false)]
    [InlineData("class C { public void M(int x, int y) => System.Console.SetCursorPosition(x, y); }", false)]
    public async Task NegativeConsoleCursorPositionArgumentAnalyzer_Should_NotFlag_ANonNegativePosition(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeConsoleCursorPositionArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeConsoleCursorPositionArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighty-sixth batch (триста тридцать второй проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', -1); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', 5); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', 3); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', -1, 1); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', 0, -1); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', 2, 5); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', 0, 3); }", false)]
    [InlineData("class C { public int M(string s) => s.IndexOf('a', -1); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", -1); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", 5); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", 3); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", -1, 1); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", 0, 3); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", -1, System.StringComparison.Ordinal); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", 3, System.StringComparison.Ordinal); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", -1, 1, System.StringComparison.Ordinal); }", true)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", 0, 3, System.StringComparison.Ordinal); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf(\"a\", System.StringComparison.Ordinal); }", false)]
    public async Task InvalidStringIndexOfCharArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringIndexOfCharArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringIndexOfCharArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighty-eighth batch (триста тридцать четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', -1); }", true)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', 3); }", true)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', 2); }", false)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', 2, -1); }", true)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', 2, 4); }", true)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf('a', 2, 3); }", false)]
    [InlineData("class C { public int M() => \"abc\".LastIndexOf(\"a\", -1, System.StringComparison.Ordinal); }", true)]
    [InlineData("class C { public int M() => \"\".LastIndexOf('a', -1); }", false)]
    [InlineData("class C { public int M() => \"abc\".IndexOf('a', -1); }", false)]
    public async Task InvalidStringLastIndexOfArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringLastIndexOfArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringLastIndexOfArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and eighty-ninth batch (триста тридцать пятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3], -1, 1); }", true)]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3], 0, -1); }", true)]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3], 2, 5); }", true)]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3], 3, 0); }", false)]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3], 0, 3); }", false)]
    [InlineData("class C { public string M() => string.Join(\",\", new string[3]); }", false)]
    [InlineData("class C { public string M() => string.Join(\",\", (System.Collections.Generic.IEnumerable<string>)new string[3]); }", false)]
    public async Task InvalidStringJoinArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringJoinArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringJoinArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and ninetieth batch (триста тридцать шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.EnsureCapacity(0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<int, int> d) => d.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(HashSet<int> h) => h.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Queue<int> q) => q.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Stack<int> s) => s.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs, int n) => xs.EnsureCapacity(-n); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<int, int> d) => d.TrimExcess(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(HashSet<int> h) => h.TrimExcess(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Queue<int> q) => q.TrimExcess(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Stack<int> s) => s.TrimExcess(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<int, int> d) => d.TrimExcess(0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(Dictionary<int, int> d) => d.TrimExcess(); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(PriorityQueue<int, int> pq) => pq.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(PriorityQueue<int, int> pq) => pq.EnsureCapacity(0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(PriorityQueue<int, int> pq) => pq.TrimExcess(); }", false)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.EnsureCapacity(-1); }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.EnsureCapacity(0); }", false)]
    public async Task NegativeEnsureCapacityArgumentAnalyzer_Should_NotFlag_ANonNegativeCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeEnsureCapacityArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeEnsureCapacityArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and ninety-fifth batch (триста сорок первый проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[5]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[-1]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[2]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveAt(5); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveAt(-1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveAt(2); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Insert(5, 0); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Insert(3, 0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.RemoveAt(5); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int[] M() => new int[3] { 1, 2, 3 }; }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveRange(0, 10); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveRange(-1, 1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveRange(0, -1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveRange(0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }.GetRange(0, 10); }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }.GetRange(0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.RemoveRange(0, 10); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[^5]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[^1]; }", false)]
    public async Task ListLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ListLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ListLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => RandomNumberGenerator.GetBytes(-1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public string M() => RandomNumberGenerator.GetHexString(-1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public string M() => RandomNumberGenerator.GetHexString(-1, true); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => RandomNumberGenerator.GetBytes(0); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(-1); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(int n) => RandomNumberGenerator.GetBytes(-n); }", false)]
    public async Task NegativeRandomNumberGeneratorArgumentAnalyzer_Should_NotFlag_ANonNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeRandomNumberGeneratorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeRandomNumberGeneratorArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and ninety-seventh batch (триста сорок второй проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(-1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(0); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(1); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(5, 5); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(5, 4); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(0, 5); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(-5, -1); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => RandomNumberGenerator.GetBytes(-1); }", false)]
    public async Task InvalidRandomNumberGeneratorGetInt32ArgumentAnalyzer_Should_NotFlag_AValidRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRandomNumberGeneratorGetInt32ArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRandomNumberGeneratorGetInt32ArgumentAnalyzer.DiagnosticId));
    }

    // ── Hundred and ninety-eighth batch (триста сорок третий проход) — three new analyzers.

    [Theory]
    [InlineData("class C { public string[] M() => \"a,b\".Split(',', -1); }", true)]
    [InlineData("class C { public string[] M() => \"a,b\".Split(',', -1, System.StringSplitOptions.None); }", true)]
    [InlineData("class C { public string[] M() => \"a,b\".Split(new[] { ',' }, -1); }", true)]
    [InlineData("class C { public string[] M() => \"a,b\".Split(',', 5); }", false)]
    [InlineData("class C { public string[] M() => \"a,b\".Split(',', System.StringSplitOptions.None); }", false)]
    [InlineData("class C { public string[] M(int n) => \"a,b\".Split(',', -n); }", false)]
    public async Task NegativeStringSplitCountArgumentAnalyzer_Should_NotFlag_ANonNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeStringSplitCountArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeStringSplitCountArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections; class C { public BitArray M() => new BitArray(-1); }", true)]
    [InlineData("using System.Collections; class C { public BitArray M() => new BitArray(0); }", false)]
    [InlineData("using System; class C { public Array M() => Array.CreateInstance(typeof(int), -1); }", true)]
    [InlineData("using System; class C { public Array M() => Array.CreateInstance(typeof(int), 5); }", false)]
    public async Task NegativeBitArrayOrArrayCreateInstanceLengthAnalyzer_Should_NotFlag_ANonNegativeLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeBitArrayOrArrayCreateInstanceLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeBitArrayOrArrayCreateInstanceLengthAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public SortedSet<int> M() => new SortedSet<int> { 1, 2, 3 }.GetViewBetween(5, 1); }", true)]
    [InlineData("using System.Collections.Generic; class C { public SortedSet<int> M() => new SortedSet<int> { 1, 2, 3 }.GetViewBetween(1, 5); }", false)]
    [InlineData("using System.Collections.Generic; class C { public SortedSet<int> M() => new SortedSet<int> { 1, 2, 3 }.GetViewBetween(1, 1); }", false)]
    public async Task InvalidSortedSetGetViewBetweenArgumentAnalyzer_Should_NotFlag_AValidRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSortedSetGetViewBetweenArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSortedSetGetViewBetweenArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and third batch (триста сорок четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public char M() => System.Convert.ToChar(\"ab\"); }", true)]
    [InlineData("class C { public char M() => System.Convert.ToChar(\"\"); }", true)]
    [InlineData("class C { public char M() => System.Convert.ToChar(\"a\"); }", false)]
    [InlineData("class C { public char M(string s) => System.Convert.ToChar(s); }", false)]
    public async Task InvalidConvertToCharArgumentAnalyzer_Should_NotFlag_ASingleCharacterString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConvertToCharArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConvertToCharArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fourth and two hundred and fifth batches (триста сорок пятый проход) — two new analyzers.

    [Theory]
    [InlineData("class C { public int M() => string.Compare(\"abc\", -1, \"def\", 0, 1); }", true)]
    [InlineData("class C { public int M() => string.Compare(\"abc\", 0, \"def\", -1, 1); }", true)]
    [InlineData("class C { public int M() => string.Compare(\"abc\", 0, \"def\", 0, -1); }", true)]
    [InlineData("class C { public int M() => string.Compare(\"abc\", 0, \"def\", 0, 2); }", false)]
    [InlineData("class C { public int M() => string.Compare(\"abc\", 0, \"def\", 0, 2, true); }", false)]
    [InlineData("class C { public int M(int i) => string.Compare(\"abc\", -i, \"def\", 0, 2); }", false)]
    public async Task NegativeStringCompareSubstringArgumentAnalyzer_Should_NotFlag_ANonNegativeArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeStringCompareSubstringArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeStringCompareSubstringArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public string M() => new Dictionary<int, string> { { 1, \"a\" }, { 2, \"b\" } }[3]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public string M() => new Dictionary<int, string> { { 1, \"a\" }, { 2, \"b\" } }[1]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public string M(Dictionary<int, string> d) => d[3]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public string M() => new Dictionary<int, string> { { 1, \"a\" } }[3]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new SortedList<string, int> { { \"a\", 1 } }[\"b\"]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new SortedList<string, int> { { \"a\", 1 } }[\"a\"]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new SortedDictionary<string, int> { { \"a\", 1 } }[\"b\"]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new SortedDictionary<string, int> { { \"a\", 1 } }[\"a\"]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(SortedList<string, int> l) => l[\"b\"]; }", false)]
    public async Task DictionaryLiteralMissingKeyAnalyzer_Should_NotFlag_APresentKey(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DictionaryLiteralMissingKeyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DictionaryLiteralMissingKeyAnalyzer.DiagnosticId));
    }

    // ── Two hundred and sixth batch (триста сорок шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public DateTime M() => new DateTime(9999, 12, 31).AddDays(1); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(1, 1, 1).AddDays(-1); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(9999, 1, 1).AddYears(1); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(9999, 12, 1).AddMonths(1); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2024, 1, 1).AddDays(1); }", false)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(9999, 12, 31, 23, 0, 0).AddHours(2); }", true)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(9999, 12, 31).AddTicks(1); }", false)]
    [InlineData("using System; class C { public DateOnly M() => new DateOnly(9999, 12, 31).AddDays(1); }", true)]
    [InlineData("using System; class C { public DateOnly M() => new DateOnly(2024, 1, 1).AddDays(1); }", false)]
    [InlineData("using System; class C { public DateTime M(DateTime dt) => dt.AddDays(1); }", false)]
    [InlineData("using System; class C { public DateTime M() => new DateTime(2024, 1, 1).AddDays(1_000_000); }", false)]
    public async Task DateTimeOrDateOnlyAddMethodOverflowAnalyzer_Should_NotFlag_AnInRangeResult(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DateTimeOrDateOnlyAddMethodOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DateTimeOrDateOnlyAddMethodOverflowAnalyzer.DiagnosticId));
    }

    // ── Two hundred and seventh and two hundred and eighth batches (триста сорок седьмой проход) — two new analyzers.

    [Theory]
    [InlineData("using System; class C { public TimeSpan M() => new TimeSpan(int.MaxValue, 0, 0); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => new TimeSpan(1, 0, 0); }", false)]
    [InlineData("using System; class C { public TimeSpan M() => new TimeSpan(int.MaxValue, 0, 0, 0); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => new TimeSpan(long.MaxValue); }", false)]
    [InlineData("using System; class C { public TimeSpan M(int h) => new TimeSpan(h, 0, 0); }", false)]
    public async Task TimeSpanConstructorOverflowAnalyzer_Should_NotFlag_AnInRangeResult(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TimeSpanConstructorOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TimeSpanConstructorOverflowAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.MaxValue + TimeSpan.FromDays(1); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.MinValue - TimeSpan.FromDays(1); }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(1) + TimeSpan.FromDays(1); }", false)]
    [InlineData("using System; class C { public TimeSpan M(TimeSpan a, TimeSpan b) => a + b; }", false)]
    public async Task TimeSpanAddOrSubtractOperatorOverflowAnalyzer_Should_NotFlag_AnInRangeResult(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TimeSpanAddOrSubtractOperatorOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TimeSpanAddOrSubtractOperatorOverflowAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fifty-first through two hundred and fifty-third batches (триста пятьдесят шестой проход) — one new analyzer + two extensions.

    [Theory]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.MaxValue * 2; }", true)]
    [InlineData("using System; class C { public TimeSpan M() => 2 * TimeSpan.MaxValue; }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(1) * 2; }", false)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(1) / 1e-30; }", true)]
    [InlineData("using System; class C { public TimeSpan M() => TimeSpan.FromDays(1) / 2; }", false)]
    [InlineData("using System; class C { public TimeSpan M() => -TimeSpan.MinValue; }", true)]
    [InlineData("using System; class C { public TimeSpan M() => -TimeSpan.MaxValue; }", false)]
    [InlineData("using System; class C { public TimeSpan M(TimeSpan a) => a * 2; }", false)]
    public async Task TimeSpanAddOrSubtractOperatorOverflowAnalyzer_Should_NotFlag_AnInRangeMultiplyDivideOrNegate(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new TimeSpanAddOrSubtractOperatorOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == TimeSpanAddOrSubtractOperatorOverflowAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public DateTime M() => DateTime.MaxValue + TimeSpan.FromDays(1); }", true)]
    [InlineData("using System; class C { public DateTime M() => DateTime.MinValue - TimeSpan.FromDays(1); }", true)]
    [InlineData("using System; class C { public DateTime M() => DateTime.MaxValue - TimeSpan.FromDays(1); }", false)]
    [InlineData("using System; class C { public DateTime M(DateTime d) => d + TimeSpan.FromDays(1); }", false)]
    public async Task DateTimeAddOrSubtractOperatorOverflowAnalyzer_Should_NotFlag_AnInRangeResult(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DateTimeAddOrSubtractOperatorOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DateTimeAddOrSubtractOperatorOverflowAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public sbyte M() => System.Convert.ToSByte(200u); }", true)]
    [InlineData("class C { public short M() => System.Convert.ToInt16(100000ul); }", true)]
    [InlineData("class C { public byte M() => System.Convert.ToByte(300u); }", true)]
    [InlineData("class C { public sbyte M() => System.Convert.ToSByte(100u); }", false)]
    [InlineData("class C { public sbyte M(uint u) => System.Convert.ToSByte(u); }", false)]
    public async Task ConvertNumericNarrowingOverflowAnalyzer_Should_NotFlag_AnInRangeUnsignedSource(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ConvertNumericNarrowingOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ConvertNumericNarrowingOverflowAnalyzer.DiagnosticId));
    }

    // ── Two hundred and eleventh through two hundred and fourteenth batches (триста сорок восьмой проход) — four new analyzers.

    [Theory]
    [InlineData("using System.Linq; class C { public int M() => new[] { int.MaxValue, 1 }.Sum(); }", true)]
    [InlineData("using System.Linq; class C { public int M() => new[] { 1, 2, 3 }.Sum(); }", false)]
    [InlineData("using System.Linq; class C { public long M() => new[] { long.MaxValue, 1L }.Sum(); }", true)]
    [InlineData("using System.Linq; class C { public int M(int[] xs) => xs.Sum(); }", false)]
    public async Task EnumerableSumOverflowAnalyzer_Should_NotFlag_AnInRangeSum(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EnumerableSumOverflowAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EnumerableSumOverflowAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M() => new[] { 1, 2, 1 }.ToDictionary(x => x); }", true)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M() => new[] { 1, 2, 3 }.ToDictionary(x => x); }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M(int[] xs) => xs.ToDictionary(x => x); }", false)]
    [InlineData("using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, string> M() => new[] { 1, 2, 1 }.ToDictionary(x => x.ToString()); }", false)]
    public async Task EnumerableToDictionaryDuplicateKeyAnalyzer_Should_NotFlag_ANoDuplicateArray(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EnumerableToDictionaryDuplicateKeyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EnumerableToDictionaryDuplicateKeyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.CopyTo(new int[2]); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.CopyTo(new int[3]); }", false)]
    [InlineData("class C { public void M() => new int[3] { 1, 2, 3 }.CopyTo(new int[5], 3); }", true)]
    [InlineData("class C { public void M() => new int[3] { 1, 2, 3 }.CopyTo(new int[3], 0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new HashSet<int> { 1, 2, 3 }.CopyTo(new int[2]); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> xs) => xs.CopyTo(new int[2]); }", false)]
    public async Task CopyToDestinationTooSmallAnalyzer_Should_NotFlag_ASufficientDestination(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CopyToDestinationTooSmallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CopyToDestinationTooSmallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(-1, 5); }", true)]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(10, 5); }", true)]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(0, 0); }", true)]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(5, 5); }", false)]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(-1); }", true)]
    [InlineData("using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(5); }", false)]
    public async Task InvalidSemaphoreSlimConstructorArgumentAnalyzer_Should_NotFlag_AValidRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSemaphoreSlimConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSemaphoreSlimConstructorArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and seventeenth and two hundred and eighteenth batches (триста сорок девятый проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.List<int> M() => new object[] { \"x\" }.Cast<int>().ToList(); }", true)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.List<int> M() => new object[] { 1, 2 }.Cast<int>().ToList(); }", false)]
    [InlineData("using System.Linq; class C { public System.Collections.Generic.IEnumerable<int> M() => new object[] { \"x\" }.Cast<int>(); }", false)]
    [InlineData("using System.Linq; class C { public int M() => new object[] { \"x\" }.Cast<int>().Count(); }", true)]
    [InlineData("using System.Linq; class C { public int[] M() => new object[] { \"x\" }.Cast<int>().ToArray(); }", true)]
    public async Task EnumerableCastInvalidElementTypeAnalyzer_Should_NotFlag_AMatchingElementType(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EnumerableCastInvalidElementTypeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EnumerableCastInvalidElementTypeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections; class C { public bool M() => new BitArray(3)[5]; }", true)]
    [InlineData("using System.Collections; class C { public bool M() => new BitArray(3)[-1]; }", true)]
    [InlineData("using System.Collections; class C { public bool M() => new BitArray(3)[2]; }", false)]
    [InlineData("using System.Collections; class C { public bool M(BitArray b) => b[5]; }", false)]
    public async Task BitArrayLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new BitArrayLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == BitArrayLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Two hundred and twentieth through two hundred and twenty-fourth batches (триста пятидесятый проход) — three new analyzers + two extensions.

    [Theory]
    [InlineData("using System.Threading; class C { public Semaphore M() => new Semaphore(-1, 5); }", true)]
    [InlineData("using System.Threading; class C { public Semaphore M() => new Semaphore(0, 0); }", true)]
    [InlineData("using System.Threading; class C { public Semaphore M() => new Semaphore(10, 5); }", true)]
    [InlineData("using System.Threading; class C { public Semaphore M() => new Semaphore(5, 5); }", false)]
    [InlineData("using System.Threading; class C { public Semaphore M(int n) => new Semaphore(n, 5); }", false)]
    public async Task InvalidSemaphoreConstructorArgumentAnalyzer_Should_NotFlag_AValidRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSemaphoreConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSemaphoreConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public CountdownEvent M() => new CountdownEvent(-1); }", true)]
    [InlineData("using System.Threading; class C { public CountdownEvent M() => new CountdownEvent(0); }", false)]
    [InlineData("using System.Threading; class C { public Barrier M() => new Barrier(-1); }", true)]
    [InlineData("using System.Threading; class C { public Barrier M() => new Barrier(-1, null); }", true)]
    [InlineData("using System.Threading; class C { public Barrier M() => new Barrier(0); }", false)]
    [InlineData("using System.Threading; class C { public CountdownEvent M(int n) => new CountdownEvent(n); }", false)]
    public async Task InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer_Should_NotFlag_ANonNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], -1, 1); }", true)]
    [InlineData("class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], 0, -1); }", true)]
    [InlineData("class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], 0, 10); }", true)]
    [InlineData("class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], 0, 3); }", false)]
    [InlineData("class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], 3, 0); }", false)]
    [InlineData("class C { public System.ArraySegment<int> M(int[] a) => new System.ArraySegment<int>(a, 0, 10); }", false)]
    public async Task InvalidArraySegmentConstructorArgumentAnalyzer_Should_NotFlag_AnInBoundsSegment(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArraySegmentConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArraySegmentConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; class C { public byte[] M() => new byte[3].AsSpan(0, 10).ToArray(); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new Span<byte>(new byte[3], -1, 1); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new Span<byte>(new byte[3], 0, 10); }", true)]
    [InlineData("using System; class C { public Span<byte> M() => new Span<byte>(new byte[3], 0, 3); }", false)]
    [InlineData("using System; class C { public Memory<byte> M() => new Memory<byte>(new byte[3], 0, 10); }", true)]
    [InlineData("using System; class C { public ReadOnlySpan<byte> M() => new ReadOnlySpan<byte>(new byte[3], 0, 10); }", true)]
    [InlineData("using System; class C { public ReadOnlyMemory<byte> M() => new ReadOnlyMemory<byte>(new byte[3], 0, 10); }", true)]
    [InlineData("using System; class C { public Span<byte> M(byte[] a) => new Span<byte>(a, 0, 10); }", false)]
    public async Task InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer_Should_NotFlag_AnInBoundsSpanOrMemory(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.CopyTo(new int[2]); }", true)]
    [InlineData("class C { public void M() => System.Array.Copy(new int[3], 0, new int[2], 0, 3); }", true)]
    [InlineData("class C { public void M() => System.Array.Copy(new int[2], new int[5], 3); }", true)]
    [InlineData("class C { public void M() => System.Array.Copy(new int[5], new int[2], 3); }", true)]
    [InlineData("class C { public void M() => System.Array.Copy(new int[5], new int[5], 3); }", false)]
    [InlineData("class C { public void M() => System.Array.Copy(new int[5], 1, new int[5], 0, 3); }", false)]
    [InlineData("class C { public void M(int[] a, int[] b) => System.Array.Copy(a, b, 3); }", false)]
    public async Task CopyToDestinationTooSmallAnalyzer_Should_NotFlag_ASufficientArrayCopy(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CopyToDestinationTooSmallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CopyToDestinationTooSmallAnalyzer.DiagnosticId));
    }

    // ── Two hundred and twenty-fifth through two hundred and twenty-ninth batches (триста пятьдесят первый проход) — five new analyzers.

    [Theory]
    [InlineData("class C { public string M() => char.ConvertFromUtf32(-1); }", true)]
    [InlineData("class C { public string M() => char.ConvertFromUtf32(0x110000); }", true)]
    [InlineData("class C { public string M() => char.ConvertFromUtf32(0xD800); }", true)]
    [InlineData("class C { public string M() => char.ConvertFromUtf32(65); }", false)]
    [InlineData("using System.Text; class C { public Rune M() => new Rune(0x110000); }", true)]
    [InlineData("using System.Text; class C { public Rune M() => new Rune(65); }", false)]
    [InlineData("class C { public string M(int cp) => char.ConvertFromUtf32(cp); }", false)]
    public async Task InvalidUnicodeCodePointArgumentAnalyzer_Should_NotFlag_AValidCodePoint(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidUnicodeCodePointArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidUnicodeCodePointArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public Rune M() => new Rune((char)0xD800); }", true)]
    [InlineData("using System.Text; class C { public Rune M() => new Rune((char)0xDFFF); }", true)]
    [InlineData("using System.Text; class C { public Rune M() => new Rune('a'); }", false)]
    [InlineData("class C { public int M() => char.ConvertToUtf32('a', 'b'); }", true)]
    [InlineData("class C { public int M() => char.ConvertToUtf32((char)0xD800, (char)0xDC00); }", false)]
    [InlineData("using System.Text; class C { public Rune M(char c) => new Rune(c); }", false)]
    public async Task InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer_Should_NotFlag_AValidCharOrPair(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public System.Index M() => new System.Index(-1); }", true)]
    [InlineData("class C { public System.Index M() => new System.Index(-1, true); }", true)]
    [InlineData("class C { public System.Index M() => new System.Index(0); }", false)]
    [InlineData("class C { public System.Index M(int n) => new System.Index(n); }", false)]
    public async Task InvalidIndexConstructorArgumentAnalyzer_Should_NotFlag_ANonNegativeValue(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIndexConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIndexConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public ManualResetEventSlim M() => new ManualResetEventSlim(false, -1); }", true)]
    [InlineData("using System.Threading; class C { public ManualResetEventSlim M() => new ManualResetEventSlim(false, 2048); }", true)]
    [InlineData("using System.Threading; class C { public ManualResetEventSlim M() => new ManualResetEventSlim(false, 2047); }", false)]
    [InlineData("using System.Threading; class C { public ManualResetEventSlim M() => new ManualResetEventSlim(false, 0); }", false)]
    [InlineData("using System.Threading; class C { public ManualResetEventSlim M(int n) => new ManualResetEventSlim(false, n); }", false)]
    public async Task InvalidManualResetEventSlimConstructorArgumentAnalyzer_Should_NotFlag_AnInRangeSpinCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidManualResetEventSlimConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidManualResetEventSlimConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, -2, 100); }", true)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, 100, -2); }", true)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, -1, 100); }", false)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, 100, 100); }", false)]
    [InlineData("using System.Threading; class C { public Timer M(int d) => new Timer(_ => { }, null, d, 100); }", false)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, -2L, 0L); }", true)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, -1L, -1L); }", false)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, 0xffffffffL, -1L); }", true)]
    [InlineData("using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, 0xfffffffeL, -1L); }", false)]
    [InlineData("using System.Threading; class C { public Timer M(long v) => new Timer(_ => { }, null, v, -1L); }", false)]
    public async Task InvalidTimerConstructorArgumentAnalyzer_Should_NotFlag_AValidDueTimeOrPeriod(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimerConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimerConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(-2, 100); }", true)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(100, -2); }", true)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(-1, 100); }", false)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(100, 100); }", false)]
    [InlineData("using System.Threading; class C { public void M(Timer t, int d) => t.Change(d, 100); }", false)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(-2L, -1L); }", true)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(-1L, 0xffffffffL); }", true)]
    [InlineData("using System.Threading; class C { public void M(Timer t) => t.Change(-1L, 0xfffffffeL); }", false)]
    [InlineData("using System.Threading; class C { public void M(Timer t, long v) => t.Change(v, -1L); }", false)]
    public async Task InvalidTimerConstructorArgumentAnalyzer_Should_NotFlag_AValidChangeArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimerConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimerConstructorArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and thirtieth through two hundred and thirty-fourth batches (триста пятьдесят второй проход) — five new analyzers.

    [Theory]
    [InlineData("using System.Threading; class C { public void M(Thread t) => t.Join(-2); }", true)]
    [InlineData("using System.Threading; class C { public void M(Thread t) => t.Join(-1); }", false)]
    [InlineData("using System.Threading; class C { public void M(Thread t) => t.Join(0); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.Wait(-2); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.Wait(0); }", false)]
    [InlineData("using System.Threading; class C { public void M(WaitHandle w) => w.WaitOne(-2); }", true)]
    [InlineData("using System.Threading; class C { public void M(WaitHandle w) => w.WaitOne(0); }", false)]
    [InlineData("using System.Threading; class C { public void M(ManualResetEventSlim m) => m.Wait(-2); }", true)]
    [InlineData("using System.Threading; class C { public void M(ManualResetEventSlim m) => m.Wait(0); }", false)]
    [InlineData("using System.Threading; class C { public void M(Thread t, int n) => t.Join(n); }", false)]
    public async Task InvalidTimeoutArgumentAnalyzer_Should_NotFlag_AValidTimeout(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimeoutArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimeoutArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public void M(object o) => Monitor.TryEnter(o, -2); }", true)]
    [InlineData("using System.Threading; class C { public void M(object o) => Monitor.TryEnter(o, -1); }", false)]
    [InlineData("using System.Threading; class C { public void M(object o) => Monitor.TryEnter(o, 0); }", false)]
    [InlineData("using System.Threading; class C { public bool M() => SpinWait.SpinUntil(() => true, -2); }", true)]
    [InlineData("using System.Threading; class C { public bool M() => SpinWait.SpinUntil(() => true, 0); }", false)]
    [InlineData("using System.Threading; class C { public void M(object o, int n) => Monitor.TryEnter(o, n); }", false)]
    public async Task InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer_Should_NotFlag_AValidTimeout(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M() => new ConcurrentDictionary<int, int>(0, 5); }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M() => new ConcurrentDictionary<int, int>(1, -1); }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M() => new ConcurrentDictionary<int, int>(-1, 5); }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M() => new ConcurrentDictionary<int, int>(1, 5); }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M(int n) => new ConcurrentDictionary<int, int>(n, 5); }", false)]
    public async Task InvalidConcurrentDictionaryConstructorArgumentAnalyzer_Should_NotFlag_AValidRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConcurrentDictionaryConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConcurrentDictionaryConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Concurrent; class C { public BlockingCollection<int> M() => new BlockingCollection<int>(-1); }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public BlockingCollection<int> M() => new BlockingCollection<int>(0); }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public BlockingCollection<int> M() => new BlockingCollection<int>(5); }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public BlockingCollection<int> M(int n) => new BlockingCollection<int>(n); }", false)]
    public async Task InvalidBlockingCollectionConstructorArgumentAnalyzer_Should_NotFlag_APositiveCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBlockingCollectionConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBlockingCollectionConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public void M(SemaphoreSlim s) => s.Release(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(SemaphoreSlim s) => s.Release(0); }", true)]
    [InlineData("using System.Threading; class C { public void M(SemaphoreSlim s) => s.Release(1); }", false)]
    [InlineData("using System.Threading; class C { public void M(Semaphore s) => s.Release(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(SemaphoreSlim s, int n) => s.Release(n); }", false)]
    public async Task InvalidSemaphoreSlimReleaseArgumentAnalyzer_Should_NotFlag_APositiveReleaseCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSemaphoreSlimReleaseArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSemaphoreSlimReleaseArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and thirty-fifth through two hundred and thirty-ninth batches (триста пятьдесят третий проход) — five new analyzers.

    [Theory]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(new byte[3], -1, 1); }", true)]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(new byte[3], 0, 10); }", true)]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(new byte[3], 0, 3); }", false)]
    [InlineData("using System.IO; class C { public MemoryStream M() => new MemoryStream(new byte[3], 1, 2); }", false)]
    [InlineData("using System.IO; class C { public MemoryStream M(byte[] a) => new MemoryStream(a, 0, 10); }", false)]
    public async Task InvalidMemoryStreamConstructorArgumentAnalyzer_Should_NotFlag_AnInBoundsSegment(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMemoryStreamConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMemoryStreamConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public int M(MemoryStream ms) => ms.Read(new byte[2], -1, 1); }", true)]
    [InlineData("using System.IO; class C { public int M(MemoryStream ms) => ms.Read(new byte[2], 0, 10); }", true)]
    [InlineData("using System.IO; class C { public int M(MemoryStream ms) => ms.Read(new byte[2], 0, 2); }", false)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.Write(new byte[2], 0, 10); }", true)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.Write(new byte[2], 0, 2); }", false)]
    [InlineData("using System.IO; class C { public int M(MemoryStream ms, byte[] a) => ms.Read(a, 0, 10); }", false)]
    public async Task InvalidStreamReadOrWriteArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStreamReadOrWriteArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStreamReadOrWriteArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.CopyTo(new MemoryStream(), -1); }", true)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.CopyTo(new MemoryStream(), 0); }", true)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.CopyTo(new MemoryStream(), 5); }", false)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms, int n) => ms.CopyTo(new MemoryStream(), n); }", false)]
    public async Task NegativeStreamCopyToBufferSizeAnalyzer_Should_NotFlag_APositiveBufferSize(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeStreamCopyToBufferSizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeStreamCopyToBufferSizeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.SetLength(-1); }", true)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.SetLength(0); }", false)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms) => ms.SetLength(5); }", false)]
    [InlineData("using System.IO; class C { public void M(MemoryStream ms, long n) => ms.SetLength(n); }", false)]
    public async Task NegativeMemoryStreamSetLengthArgumentAnalyzer_Should_NotFlag_ANonNegativeLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeMemoryStreamSetLengthArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeMemoryStreamSetLengthArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public string M() => string.Create(-5, 0, (System.Span<char> span, int x) => { }); }", true)]
    [InlineData("class C { public string M() => string.Create(0, 0, (System.Span<char> span, int x) => { }); }", false)]
    [InlineData("class C { public string M() => string.Create(5, 0, (System.Span<char> span, int x) => { }); }", false)]
    [InlineData("class C { public string M(int n) => string.Create(n, 0, (System.Span<char> span, int x) => { }); }", false)]
    public async Task NegativeStringCreateLengthArgumentAnalyzer_Should_NotFlag_ANonNegativeLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new NegativeStringCreateLengthArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == NegativeStringCreateLengthArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fortieth through two hundred and forty-fourth batches (триста пятьдесят четвёртый проход) — one new analyzer + four extension features.

    [Theory]
    [InlineData("class C { public decimal M() => new decimal(new int[] { 1, 2, 3 }); }", true)]
    [InlineData("class C { public decimal M() => new decimal(new int[] { 1, 2, 3, 4, 5 }); }", true)]
    [InlineData("class C { public decimal M() => new decimal(new int[] { 1, 2, 3, 0 }); }", false)]
    [InlineData("class C { public decimal M(int[] bits) => new decimal(bits); }", false)]
    public async Task InvalidDecimalBitsConstructorArgumentAnalyzer_Should_NotFlag_AValidBitsArray(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDecimalBitsConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDecimalBitsConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.CopyTo(0, new char[2], 0, 10); }", true)]
    [InlineData("using System.Text; class C { public void M(StringBuilder sb) => sb.CopyTo(0, new char[5], 0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> list) => list.CopyTo(0, new int[2], 0, 10); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> list) => list.CopyTo(0, new int[5], 0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> list) => list.CopyTo(new int[3]); }", false)]
    public async Task CopyToDestinationTooSmallAnalyzer_Should_NotFlag_ASufficientFourArgCopyTo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CopyToDestinationTooSmallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CopyToDestinationTooSmallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public void M() => new Queue<int>(new[] { 1, 2, 3 }).CopyTo(new int[2], 0); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new Queue<int>(new[] { 1, 2, 3 }).CopyTo(new int[5], 0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new Stack<int>(new[] { 1, 2, 3 }).CopyTo(new int[2], 0); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new Stack<int>(new[] { 1, 2, 3 }).CopyTo(new int[5], 0); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(Queue<int> q) => q.CopyTo(new int[2], 0); }", false)]
    public async Task CopyToDestinationTooSmallAnalyzer_Should_NotFlag_ASufficientQueueOrStackCopyTo(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new CopyToDestinationTooSmallAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == CopyToDestinationTooSmallAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.BinarySearch(-1, 2, 1, null); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.BinarySearch(0, 10, 1, null); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.BinarySearch(0, 3, 1, null); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.InsertRange(-1, new[] { 1 }); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.InsertRange(10, new[] { 1 }); }", true)]
    [InlineData("using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.InsertRange(3, new[] { 1 }); }", false)]
    [InlineData("using System.Collections.Generic; class C { public void M(List<int> l) => l.InsertRange(10, new[] { 1 }); }", false)]
    public async Task ListLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInBoundsBinarySearchOrInsertRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ListLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ListLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Two hundred and forty-fifth through two hundred and forty-ninth batches (триста пятьдесят пятый проход) — three new analyzers + two extension features.

    [Theory]
    [InlineData("using System.Buffers; class C { public ArrayPool<int> M() => ArrayPool<int>.Create(-1, 10); }", true)]
    [InlineData("using System.Buffers; class C { public ArrayPool<int> M() => ArrayPool<int>.Create(10, -1); }", true)]
    [InlineData("using System.Buffers; class C { public ArrayPool<int> M() => ArrayPool<int>.Create(10, 10); }", false)]
    [InlineData("using System.Buffers; class C { public ArrayPool<int> M(int n) => ArrayPool<int>.Create(n, 10); }", false)]
    public async Task InvalidArrayPoolCreateArgumentAnalyzer_Should_NotFlag_APositiveArgument(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayPoolCreateArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayPoolCreateArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => new CultureInfo(-1); }", true)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => new CultureInfo(1033); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => new CultureInfo(0); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M(int n) => new CultureInfo(n); }", false)]
    public async Task InvalidCultureInfoLcidConstructorArgumentAnalyzer_Should_NotFlag_ANonNegativeLcid(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCultureInfoLcidConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCultureInfoLcidConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public int M(Encoding e) => e.GetByteCount(new char[3], 0, 10); }", true)]
    [InlineData("using System.Text; class C { public int M(Encoding e) => e.GetByteCount(new char[3], 0, 3); }", false)]
    [InlineData("using System.Text; class C { public int M(Encoding e) => e.GetCharCount(new byte[3], 0, 10); }", true)]
    [InlineData("using System.Text; class C { public char[] M(Encoding e) => e.GetChars(new byte[3], 0, 10); }", true)]
    [InlineData("using System.Text; class C { public int M(Encoding e) => e.GetBytes(new char[3], 0, 10, new byte[5], 0); }", true)]
    [InlineData("using System.Text; class C { public int M(Encoding e) => e.GetBytes(new char[3], 0, 3, new byte[5], 0); }", false)]
    [InlineData("using System.Text; class C { public int M(Encoding e, char[] a) => e.GetByteCount(a, 0, 10); }", false)]
    public async Task InvalidEncodingGetStringArgumentAnalyzer_Should_NotFlag_AnInBoundsCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEncodingGetStringArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEncodingGetStringArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.IndexOf(1, -1, 2); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.IndexOf(1, 0, 10); }", true)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.IndexOf(1, 0, 3); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.IndexOf(1); }", false)]
    [InlineData("using System.Collections.Generic; class C { public int M(List<int> l) => l.IndexOf(1, 0, 10); }", false)]
    public async Task ListLiteralIndexerOutOfRangeAnalyzer_Should_NotFlag_AnInBoundsIndexOf(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new ListLiteralIndexerOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == ListLiteralIndexerOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fifty-fourth batch (триста пятьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"abc\"); }", true)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"zz\"); }", true)]
    [InlineData("using System; class C { public byte[] M() => Convert.FromHexString(\"ab\"); }", false)]
    [InlineData("using System; class C { public byte[] M(string s) => Convert.FromHexString(s); }", false)]
    public async Task InvalidHexStringLiteralAnalyzer_Should_NotFlag_AValidHexString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHexStringLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHexStringLiteralAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fifty-fifth batch (триста пятьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M(int n) => n.ToString(\"Z\"); }", true)]
    [InlineData("class C { public string M() => 42.ToString(\"X\"); }", false)]
    [InlineData("class C { public string M() => 42.ToString(\"G\"); }", false)]
    [InlineData("class C { public string M(double d) => d.ToString(\"X\"); }", true)]
    [InlineData("class C { public string M(double d) => d.ToString(\"F2\"); }", false)]
    [InlineData("using System; class C { public string M() => TimeSpan.Zero.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M() => TimeSpan.Zero.ToString(\"c\"); }", false)]
    [InlineData("using System; class C { public string M() => DateTime.MinValue.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M() => DateTime.MinValue.ToString(\"O\"); }", false)]
    [InlineData("class C { public string M(int n, string f) => n.ToString(f); }", false)]
    [InlineData("using System; class C { public string M() => Guid.Empty.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M() => Guid.Empty.ToString(\"D\"); }", false)]
    [InlineData("using System; class C { public string M() => DateOnly.MinValue.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M() => DateOnly.MinValue.ToString(\"d\"); }", false)]
    [InlineData("using System; class C { public string M() => TimeOnly.MinValue.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M() => TimeOnly.MinValue.ToString(\"t\"); }", false)]
    [InlineData("using System; class C { public string M(DateTimeOffset d) => d.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M(DateTimeOffset d) => d.ToString(\"s\"); }", false)]
    [InlineData("using System.Numerics; class C { public string M(BigInteger b) => b.ToString(\"Z\"); }", true)]
    [InlineData("using System.Numerics; class C { public string M(BigInteger b) => b.ToString(\"X\"); }", false)]
    [InlineData("using System; class C { public string M(Int128 i) => i.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M(Int128 i) => i.ToString(\"X\"); }", false)]
    [InlineData("using System; class C { public string M(UInt128 i) => i.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M(Half h) => h.ToString(\"Z\"); }", true)]
    [InlineData("using System; class C { public string M(Half h) => h.ToString(\"F\"); }", false)]
    public async Task InvalidToStringFormatSpecifierAnalyzer_Should_NotFlag_AValidFormatSpecifier(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidToStringFormatSpecifierAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidToStringFormatSpecifierAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fifty-seventh and two hundred and fifty-eighth batches (триста шестидесятый проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Parse(\"abc\"); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Parse(\"\"); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Parse(\"12.5\"); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Parse(\"-123\"); }", false)]
    [InlineData("using System.Numerics; class C { public BigInteger M(string s) => BigInteger.Parse(s); }", false)]
    public async Task InvalidBigIntegerLiteralAnalyzer_Should_NotFlag_AValidIntegerLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBigIntegerLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBigIntegerLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Net; class C { public IPEndPoint M() => new IPEndPoint(IPAddress.Loopback, -1); }", true)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => new IPEndPoint(IPAddress.Loopback, 70000); }", true)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => new IPEndPoint(IPAddress.Loopback, 0); }", false)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => new IPEndPoint(IPAddress.Loopback, 65535); }", false)]
    [InlineData("using System.Net; class C { public IPEndPoint M(int p) => new IPEndPoint(IPAddress.Loopback, p); }", false)]
    public async Task InvalidIPEndPointConstructorArgumentAnalyzer_Should_NotFlag_AValidPort(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIPEndPointConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIPEndPointConstructorArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and fifty-ninth through two hundred and sixty-third batches (триста шестьдесят первый проход) — four new analyzers + one extension.

    [Theory]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Pow(2, -1); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.Pow(2, 5); }", false)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.ModPow(2, -1, 5); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.ModPow(2, 3, 5); }", false)]
    [InlineData("using System.Numerics; class C { public BigInteger M(int e) => BigInteger.Pow(2, e); }", false)]
    // Extended проход 439 (TODO #74, candidate #5 from the sixth research subagent survey) — ModPow zero modulus.
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.ModPow(2, 3, 0); }", true)]
    [InlineData("using System.Numerics; class C { public BigInteger M() => BigInteger.ModPow(2, 3, -5); }", false)]
    [InlineData("using System.Numerics; class C { public BigInteger M(BigInteger m) => BigInteger.ModPow(2, 3, m); }", false)]
    public async Task InvalidBigIntegerPowExponentAnalyzer_Should_NotFlag_ANonNegativeExponent(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBigIntegerPowExponentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBigIntegerPowExponentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Channels; class C { public Channel<int> M() => Channel.CreateBounded<int>(-1); }", true)]
    [InlineData("using System.Threading.Channels; class C { public Channel<int> M() => Channel.CreateBounded<int>(0); }", false)]
    [InlineData("using System.Threading.Channels; class C { public Channel<int> M() => Channel.CreateBounded<int>(5); }", false)]
    [InlineData("using System.Threading.Channels; class C { public Channel<int> M(int n) => Channel.CreateBounded<int>(n); }", false)]
    public async Task InvalidChannelCreateBoundedArgumentAnalyzer_Should_NotFlag_ANonNegativeCapacity(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidChannelCreateBoundedArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidChannelCreateBoundedArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(Aes aes) => aes.KeySize = 100; }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(Aes aes) => aes.KeySize = 128; }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(Aes aes) => aes.BlockSize = 256; }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(Aes aes) => aes.BlockSize = 128; }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DES des) => des.KeySize = 128; }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DES des) => des.KeySize = 64; }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(TripleDES tdes) => tdes.KeySize = 100; }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.KeySize = 100; }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.KeySize = 2048; }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(Aes aes, int n) => aes.KeySize = n; }", false)]
    // RSA.Create(int) static factory, added триста восемьдесят первый проход.
    [InlineData("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(1023); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(2048); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(\"RSA\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public RSA M(int n) => RSA.Create(n); }", false)]
    // DSA.Create(int) static factory, added 470-й проход.
    [InlineData("using System.Security.Cryptography; class C { public DSA M() => DSA.Create(-1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public DSA M() => DSA.Create(0); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public DSA M() => DSA.Create(1024); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public DSA M(int n) => DSA.Create(n); }", false)]
    public async Task InvalidCryptoAlgorithmKeySizeAnalyzer_Should_NotFlag_AValidSize(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCryptoAlgorithmKeySizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCryptoAlgorithmKeySizeAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixth batch (триста восемьдесят второй проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\"\"); }", true)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\"NoCommaHere\"); }", true)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\".NETFramework,Version=v4.5\"); }", false)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\".NETFramework,Version=v4.0,Profile=Client\"); }", false)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\".NETFramework,Version=\"); }", true)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\".NETFramework,Version=abc\"); }", true)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\".NETFramework,Version=v4.5,Profile=Client,Extra=Field\"); }", true)]
    [InlineData("using System.Runtime.Versioning; class C { public FrameworkName M(string x) => new FrameworkName(x); }", false)]
    [InlineData("using System; using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\"id\", new Version(1, 0)); }", false)]
    public async Task InvalidFrameworkNameLiteralAnalyzer_Should_NotFlag_AValidFrameworkNameString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidFrameworkNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidFrameworkNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and seventh batch (триста восемьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\"\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\"   \"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\"attachment\"); }", false)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\"attachment; filename=foo.txt\"); }", false)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\";;;\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\"attachment; filename=\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M(string x) => new ContentDisposition(x); }", false)]
    [InlineData("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(); }", false)]
    public async Task InvalidContentDispositionLiteralAnalyzer_Should_NotFlag_AValidContentDispositionString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidContentDispositionLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidContentDispositionLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and eighth batch (триста восемьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.com\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"example.com\"); } }", false)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"a..b\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\".\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"-example.com\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"foo_bar.com\"); } }", false)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping { UseStd3AsciiRules = true }; return idn.GetAscii(\"foo_bar.com\"); } }", false)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetUnicode(\"\"); } }", true)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetUnicode(\"xn--nxasmq6b\"); } }", false)]
    [InlineData("using System.Globalization; class C { public string M(string x) { var idn = new IdnMapping(); return idn.GetAscii(x); } }", false)]
    [InlineData("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"\", 0); } }", false)]
    public async Task InvalidIdnMappingLiteralAnalyzer_Should_NotFlag_AValidIdnLabel(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIdnMappingLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIdnMappingLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and ninth batch (триста восемьдесят пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"\"); }", true)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"///bad[\"); }", true)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"foo[@bar='\"); }", true)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"foo::bar::baz(\"); }", true)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"//foo\"); }", false)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"1+1\"); }", false)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"count(//foo)\"); }", false)]
    [InlineData("using System.Xml.XPath; class C { public XPathExpression M(string x) => XPathExpression.Compile(x); }", false)]
    public async Task InvalidXPathExpressionLiteralAnalyzer_Should_NotFlag_AValidXPathExpression(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXPathExpressionLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXPathExpressionLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and tenth batch (триста восемьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"garbage\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"1\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<1,2>\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<1; 2\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<1; 2>\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<1;2>\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<NaN; 2>\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"<1e400; 2>\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M(string x) => Complex.Parse(x, CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"\", NumberStyles.Any, CultureInfo.InvariantCulture); }", false)]
    public async Task InvalidComplexParseLiteralAnalyzer_Should_NotFlag_AValidComplexString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidComplexParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidComplexParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and eleventh batch (триста восемьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"\", \"value\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"bad=name\", \"value\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"bad,name\", \"value\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"bad;name\", \"value\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"$reserved\", \"value\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"name\", \"value\"); }", false)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"name\", \"value with spaces\"); }", false)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"bad=name\", \"value\", \"/path\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(\"bad=name\", \"value\", \"/path\", \"example.com\"); }", true)]
    [InlineData("using System.Net; class C { public Cookie M() => new Cookie(); }", false)]
    [InlineData("using System.Net; class C { public Cookie M(string n) => new Cookie(n, \"value\"); }", false)]
    public async Task InvalidCookieNameLiteralAnalyzer_Should_NotFlag_AValidCookieName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCookieNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCookieNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twelfth batch (триста восемьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"\"); }", true)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"Not/A/Real/Zone\"); }", true)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"UTC\"); }", false)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"America/New_York\"); }", false)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"Eastern Standard Time\"); }", false)]
    [InlineData("class C { public System.TimeZoneInfo M(string id) => System.TimeZoneInfo.FindSystemTimeZoneById(id); }", false)]
    public async Task InvalidTimeZoneIdLiteralAnalyzer_Should_NotFlag_ARecognizedTimeZoneId(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimeZoneIdLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimeZoneIdLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirteenth batch (триста восемьдесят девятый проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"\"); }", true)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"   \"); }", true)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"GET POST\"); }", true)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"GET/POST\"); }", true)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"GET\"); }", false)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"get\"); }", false)]
    [InlineData("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"CUSTOM\"); }", false)]
    [InlineData("class C { public System.Net.Http.HttpMethod M(string x) => new System.Net.Http.HttpMethod(x); }", false)]
    public async Task InvalidHttpMethodLiteralAnalyzer_Should_NotFlag_AValidHttpMethodToken(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHttpMethodLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHttpMethodLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fourteenth batch (триста девяностый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"garbage no equals\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"=novalue\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\",CN=test\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"CN=test,,O=Org\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"UNKNOWNKEY=test\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"CN=test\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"CN=test,O=Org,C=US\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"CN=\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"CN=test,\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M(string x) => new X500DistinguishedName(x); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"garbage no equals\", X500DistinguishedNameFlags.None); }", false)]
    public async Task InvalidX500DistinguishedNameLiteralAnalyzer_Should_NotFlag_AValidDistinguishedName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX500DistinguishedNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX500DistinguishedNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifteenth batch (триста девяносто первый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"=noKey\"; }", true)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"key\"; }", true)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"key=value\"; }", false)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"key=value;key2=value2\"; }", false)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"\"; }", false)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"key=value;;;\"; }", false)]
    [InlineData("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b, string x) => b.ConnectionString = x; }", false)]
    [InlineData("class Foo { public string ConnectionString { get; set; } } class C { public void M(Foo f) => f.ConnectionString = \"=noKey\"; }", false)]
    [InlineData("using System.Data.Common; class MyBuilder : DbConnectionStringBuilder { } class C { public void M(MyBuilder b) => b.ConnectionString = \"=noKey\"; }", false)]
    public async Task InvalidDbConnectionStringLiteralAnalyzer_Should_NotFlag_AWellFormedConnectionString(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDbConnectionStringLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDbConnectionStringLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixteenth batch (триста девяносто второй проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"1bad\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"has space\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"validName\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"with-dash\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNCName(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNCName(\"has:colon\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNCName(\"valid\"); }", false)]
    [InlineData("using System.Xml; class C { public string M(string x) => XmlConvert.VerifyName(x); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.ToString(1); }", false)]
    public async Task InvalidXmlNameLiteralAnalyzer_Should_NotFlag_AValidXmlName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and seventeenth batch (триста девяносто третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"\"); }", true)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly, Version=bad\"); }", true)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly, PublicKeyToken=zz\"); }", true)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"   \"); }", true)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly\"); }", false)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly, Version=1.0.0.0\"); }", false)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"///invalid???\"); }", false)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly, Culture=badculture\"); }", false)]
    [InlineData("using System.Reflection; class C { public AssemblyName M(string x) => new AssemblyName(x); }", false)]
    [InlineData("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(); }", false)]
    public async Task InvalidAssemblyNameLiteralAnalyzer_Should_NotFlag_AValidAssemblyDisplayName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAssemblyNameLiteralAnalyzer(), code);
        // An analyzer exception surfaces as AD0001, not RPAD0385 — reject it so a crash can't pass a
        // false expectation (e.g. `Culture=badculture` under Windows/invariant globalization).
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidAssemblyNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and eighteenth batch (триста девяносто четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"{}localname\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"{unterminated\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"}badstart\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"localname\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"{namespace}localname\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"{outer{inner}}bad\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XName M(string x) => XName.Get(x); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XNamespace M() => XNamespace.Get(\"\"); }", false)]
    public async Task InvalidXNameLiteralAnalyzer_Should_NotFlag_AValidExpandedXmlName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and nineteenth batch (триста девяносто пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromFriendlyName(\"\", OidGroup.All); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromFriendlyName(\"not-a-real-friendly-name\", OidGroup.All); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromFriendlyName(\"sha256\", OidGroup.All); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromFriendlyName(\"sha256\", OidGroup.HashAlgorithm); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M(string x) => Oid.FromFriendlyName(x, OidGroup.All); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => new Oid(\"not-a-real-friendly-name\"); }", false)]
    // FromOidValue, extended 474-й проход.
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromOidValue(\"1.2.3\", OidGroup.All); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromOidValue(\"\", OidGroup.All); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromOidValue(\"2.5.4.3\", OidGroup.All); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M() => Oid.FromOidValue(\"2.5.4.3\", OidGroup.HashAlgorithm); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public Oid M(string x) => Oid.FromOidValue(x, OidGroup.All); }", false)]
    public async Task InvalidOidFriendlyNameLiteralAnalyzer_Should_NotFlag_ARecognizedFriendlyName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidOidFriendlyNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidOidFriendlyNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twentieth batch (триста девяносто шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"garbage\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"1700-01-01\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"1752-12-31\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"1753-01-01\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"2024-01-01\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"9999-12-31\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDateTime M(string x) => SqlDateTime.Parse(x); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public System.DateTime M() => System.DateTime.Parse(\"garbage\"); }", false)]
    public async Task InvalidSqlDateTimeParseLiteralAnalyzer_Should_NotFlag_AValidSqlDateTime(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlDateTimeParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlDateTimeParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-first batch (триста девяносто седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"True\"); }", true)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"yes\"); }", true)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"2\"); }", true)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"true\"); }", false)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"false\"); }", false)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"1\"); }", false)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"0\"); }", false)]
    [InlineData("using System.Xml; class C { public bool M(string x) => XmlConvert.ToBoolean(x); }", false)]
    [InlineData("using System.Xml; class C { public bool M() => bool.Parse(\"True\"); }", false)]
    public async Task InvalidXmlBooleanLiteralAnalyzer_Should_NotFlag_AValidXmlBoolean(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlBooleanLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlBooleanLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-second batch (триста девяносто восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"garbage\"); }", true)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"1.02:03:04\"); }", true)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"1:30:00\"); }", true)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"P1D\"); }", false)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"PT1H30M\"); }", false)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"P1DT2H3M4S\"); }", false)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"-P1D\"); }", false)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M(string x) => XmlConvert.ToTimeSpan(x); }", false)]
    [InlineData("using System.Xml; class C { public System.TimeSpan M() => System.TimeSpan.Parse(\"garbage\"); }", false)]
    public async Task InvalidXmlTimeSpanLiteralAnalyzer_Should_NotFlag_AValidXsdDuration(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlTimeSpanLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlTimeSpanLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-third batch (триста девяносто девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"garbage\"); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-13-01T10:00:00Z\"); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-01-01T25:00:00Z\"); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"1/1/2024\"); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-01-01\"); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-01-01T10:00:00\"); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-01-01T10:00:00-05:00\"); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"2024-01-01T10:00:00Z\"); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M(string x) => XmlConvert.ToDateTimeOffset(x); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"garbage\", \"yyyy\"); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => System.DateTimeOffset.Parse(\"1/1/2024\"); }", false)]
    public async Task InvalidXmlDateTimeOffsetLiteralAnalyzer_Should_NotFlag_AValidXsdDateTime(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlDateTimeOffsetLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlDateTimeOffsetLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-fourth batch (четырёхсотый проход) — one new analyzer.

    private const string ValidRsaPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEArW0STlO8gSSjs8bp9pV9\n" +
        "pLv3Gtp/uOPw6Cavs4MxsNP9IDI/J4+NXBvhfUW97rCBgsBk8zWxCt3MOZ4lNKe6\n" +
        "acER77PFeP+jTvAKKB2uy74wyiZpIale1l9v3pzurewPbviHPKWhQVmAKAuu0w48\n" +
        "hRN6KGDKNcvvM8WdtaMUP86HW0uK4Mxg/ef8rl7J8p+sF5ZyKNjNFiqby9kyailK\n" +
        "uc1JhfEAAO5A/CIyeehBYZRoDlGeHOzkXbTg1452HY7lcjYzN44+wwlvrBf6NBxQ\n" +
        "dbcauzwnMHAvzukXLKGE0Y8pPRv11E7bpNAOzorDldOwPCbfDKslg8A8NON7jAJA\n" +
        "JQIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\"not a pem\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\"-----BEGIN CERTIFICATE-----\\nABCD\\n-----END CERTIFICATE-----\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa, string x) => rsa.ImportFromPem(x); }", false)]
    public async Task InvalidRsaPemLiteralAnalyzer_Should_NotFlag_AValidPemLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRsaPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRsaPemLiteralAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task InvalidRsaPemLiteralAnalyzer_Should_NotFlag_ARealValidRsaPublicKeyPem()
    {
        var code = "using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\""
            + ValidRsaPublicKeyPem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidRsaPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidRsaPemLiteralAnalyzer.DiagnosticId);
    }

    // проход 408 extension: a structurally-recognized PEM (generic "PUBLIC KEY" label) whose key data is
    // for a DIFFERENT algorithm (a real EC public key here) throws CryptographicException, not
    // ArgumentException — discovered while verifying ECDsa's sibling analyzer (RPAD0400).
    private const string ValidEcPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIGbMBAGByqGSM49AgEGBSuBBAAjA4GGAAQAe+iaIW7ht+I1DnpXAhdAqh0rEmN/\n" +
        "rE+oFQfVdOpxNhL9903TV+H/YDR3Kwt7D5JlYxFSbndplMkg3N7nhahbFKABxZuM\n" +
        "YZ59bicIXYxdNUUNQhnZ3/aAdFDgBHl0y84AhOd4jrDBxgrYZMyNKo+H2A6h1WzW\n" +
        "vzPZAnDDTqvUTWh3VTQ=\n" +
        "-----END PUBLIC KEY-----";

    [Fact]
    public async Task InvalidRsaPemLiteralAnalyzer_Should_Flag_AnEcKeyUnderAGenericLabel_WithCryptographicException()
    {
        var code = "using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\""
            + ValidEcPublicKeyPem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidRsaPemLiteralAnalyzer(), code);
        var match = diagnostics.FirstOrDefault(d => d.Id == InvalidRsaPemLiteralAnalyzer.DiagnosticId);
        Assert.NotNull(match);
        Assert.Contains("always throws CryptographicException at runtime", match!.GetMessage(), StringComparison.Ordinal);
    }

    // ── Three hundred and twenty-fifth batch (четыреста первый проход) — one new analyzer.

    private const string ValidCertificatePem =
        "-----BEGIN CERTIFICATE-----\n" +
        "MIICvDCCAaSgAwIBAgIIU0VdoqRWyA4wDQYJKoZIhvcNAQELBQAwHjEcMBoGA1UE\n" +
        "AxMTdGhyb3dhd2F5LXRlc3QtY2VydDAeFw0yNjA4MTIyMTI0MDlaFw0yNjA4MTQy\n" +
        "MTI0MDlaMB4xHDAaBgNVBAMTE3Rocm93YXdheS10ZXN0LWNlcnQwggEiMA0GCSqG\n" +
        "SIb3DQEBAQUAA4IBDwAwggEKAoIBAQCzN1mv94Cy4auaWuQZKvtSOB0fqxD5cUGh\n" +
        "tAuio7S4JUNffonCRqlph0SRCB/OZ1UwFMVoMRR9OCjRqJ2sYtF58HdmiNmfdWNc\n" +
        "/J683MAPfo5+2VUCt3nvCJw4cVTsJtxesXnRm52FCEXB3mfbMDvwlXCglfuNWrtc\n" +
        "a8Wt8tmhVVGLY44vqkvrkT4/RDpZDyW7Zvdpak0KJcssHm3LhHpvWGLsnSoj+fKB\n" +
        "ZZPwGIAG3erj1ti7kTbx5mV9rHBNLiWpJUHfaGev4yzJYycMOEHXqsfLTEkMI3N4\n" +
        "MbMNsh2UUpg70A+LZs1uNYCwrCIv+ZNzuAhu822J9DCVjJEFYQkdAgMBAAEwDQYJ\n" +
        "KoZIhvcNAQELBQADggEBAH9Ru5RRqamZULoxjlIjsj8n7VCzm/bSFDJE7XIARGVU\n" +
        "dKf180IXwnQxcDdKtXOVdvEKukSh+eFfTbkpKWL7QxnGihEZ/+F4ZWKCW3Zz7RY1\n" +
        "l2OwUO0jklc9YWco6YFQsuRjCT7VQtDzbprw/NFQZxGOvS7P/1MQoVNjaJo2X5hG\n" +
        "Afpv7vkJW2GrI7/nK0gOknziAOzolz5oyG0u2koW43Jrw/02gAWBYN3j5maXenuc\n" +
        "8AHgWdPzxrj+RhzbXsBfxoiQb5/KmCfijdXH1KvpoQgF/riRZAnO2L6FKGifisvS\n" +
        "jlo0hq3gV1kzQthKBEa9IGTdIofYcJOJh9f2C16kn7o=\n" +
        "-----END CERTIFICATE-----";

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\"not a pem\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\"-----BEGIN PUBLIC KEY-----\\nABCD\\n-----END PUBLIC KEY-----\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M(string x) => X509Certificate2.CreateFromPem(x); }", false)]
    // 2-arg overload, extended 473-й проход — "" as certPem always throws regardless of keyPem (cert
    // parsing happens first), so this is now flagged, unlike before this overload was covered at all.
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\"\", \"\"); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M(string k) => X509Certificate2.CreateFromPem(\"\", k); }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M(string c) => X509Certificate2.CreateFromPem(c, \"\"); }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M(string c, string k) => X509Certificate2.CreateFromPem(c, k); }", false)]
    public async Task InvalidCertificatePemLiteralAnalyzer_Should_NotFlag_AValidPemLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCertificatePemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCertificatePemLiteralAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task InvalidCertificatePemLiteralAnalyzer_Should_NotFlag_ARealValidCertificatePem()
    {
        var code = "using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\""
            + ValidCertificatePem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidCertificatePemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidCertificatePemLiteralAnalyzer.DiagnosticId);
    }

    // ── Three hundred and twenty-sixth batch (четыреста второй проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyTOKEN(\" leading\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyTOKEN(\"double  space\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyTOKEN(\"\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyTOKEN(\"1startsWithDigit\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyTOKEN(\"valid\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNMTOKEN(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNMTOKEN(\"has space\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNMTOKEN(\"1startsWithDigit\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyNMTOKEN(\"has:colon\"); }", false)]
    [InlineData("using System.Xml; class C { public string M(string x) => XmlConvert.VerifyTOKEN(x); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"1bad\"); }", false)]
    public async Task InvalidXmlTokenLiteralAnalyzer_Should_NotFlag_AValidXmlToken(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlTokenLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlTokenLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-seventh batch (четыреста третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"garbage\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"922337203685477.5808\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"-922337203685477.5809\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"123.45\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"922337203685477.5807\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"-922337203685477.5808\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlMoney M(string x) => SqlMoney.Parse(x); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public decimal M() => decimal.Parse(\"garbage\"); }", false)]
    public async Task InvalidSqlMoneyParseLiteralAnalyzer_Should_NotFlag_AValidSqlMoney(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlMoneyParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlMoneyParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-eighth batch (четыреста четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"garbage\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"999999999999999999999999999999999999999\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"99999999999999999999999999999999999999.9\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"99999999999999999999999999999999999999\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"9999999999999999999999999999999999999.9\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"123.45\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDecimal M(string x) => SqlDecimal.Parse(x); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public decimal M() => decimal.Parse(\"garbage\"); }", false)]
    public async Task InvalidSqlDecimalParseLiteralAnalyzer_Should_NotFlag_AValidSqlDecimal(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlDecimalParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlDecimalParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and twenty-ninth batch (четыреста пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"\"); }", true)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"not-an-email\"); }", true)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"a@b.com, not-an-email\"); }", true)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"a@b.com,,c@d.com\"); }", true)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"a@b.com\"); }", false)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"a@b.com, c@d.com\"); }", false)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c, string x) => c.Add(x); }", false)]
    [InlineData("using System.Net.Mail; class C { public void M(MailAddressCollection c, MailAddress a) => c.Add(a); }", false)]
    public async Task InvalidMailAddressCollectionLiteralAnalyzer_Should_NotFlag_AValidAddressList(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMailAddressCollectionLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMailAddressCollectionLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirtieth batch (четыреста шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"bad\\u0000char\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"bad\\u000Bchar\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"bad\\u000Cchar\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"bad\\u0007char\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"valid text\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"line1\\nline2\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"col1\\tcol2\"); }", false)]
    [InlineData("using System.Xml; class C { public string M(string x) => XmlConvert.VerifyXmlChars(x); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"1bad\"); }", false)]
    public async Task InvalidXmlCharsLiteralAnalyzer_Should_NotFlag_LegalXmlText(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlCharsLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlCharsLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirty-first batch (четыреста седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"xml\", \"data\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"XML\", \"data\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"1bad\", \"data\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"valid-target\", \"data\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"valid\", \"data ?> end\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XProcessingInstruction M(string x) => new XProcessingInstruction(x, \"data\"); }", false)]
    public async Task InvalidXProcessingInstructionLiteralAnalyzer_Should_NotFlag_AValidTarget(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXProcessingInstructionLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXProcessingInstructionLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirty-second batch (четыреста девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\"not a pem\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDiffieHellman ec) => ec.ImportFromPem(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec, string x) => ec.ImportFromPem(x); }", false)]
    public async Task InvalidEcAlgorithmPemLiteralAnalyzer_Should_NotFlag_AValidPemLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEcAlgorithmPemLiteralAnalyzer.DiagnosticId));
    }

    private const string ValidEcPublicKeyPemForEcAlgorithm =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIGbMBAGByqGSM49AgEGBSuBBAAjA4GGAAQAe+iaIW7ht+I1DnpXAhdAqh0rEmN/\n" +
        "rE+oFQfVdOpxNhL9903TV+H/YDR3Kwt7D5JlYxFSbndplMkg3N7nhahbFKABxZuM\n" +
        "YZ59bicIXYxdNUUNQhnZ3/aAdFDgBHl0y84AhOd4jrDBxgrYZMyNKo+H2A6h1WzW\n" +
        "vzPZAnDDTqvUTWh3VTQ=\n" +
        "-----END PUBLIC KEY-----";

    [Fact]
    public async Task InvalidEcAlgorithmPemLiteralAnalyzer_Should_NotFlag_ARealValidEcPublicKeyPem()
    {
        var code = "using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\""
            + ValidEcPublicKeyPemForEcAlgorithm.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidEcAlgorithmPemLiteralAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task InvalidEcAlgorithmPemLiteralAnalyzer_Should_Flag_ARsaKeyUnderAGenericLabel_WithCryptographicException()
    {
        var code = "using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\""
            + ValidRsaPublicKeyPem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmPemLiteralAnalyzer(), code);
        var match = diagnostics.FirstOrDefault(d => d.Id == InvalidEcAlgorithmPemLiteralAnalyzer.DiagnosticId);
        Assert.NotNull(match);
        Assert.Contains("always throws CryptographicException at runtime", match!.GetMessage(), StringComparison.Ordinal);
    }

    // ── Three hundred and thirty-third batch (четыреста десятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\"not a pem\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa, string x) => dsa.ImportFromPem(x); }", false)]
    public async Task InvalidDsaPemLiteralAnalyzer_Should_NotFlag_AValidPemLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDsaPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDsaPemLiteralAnalyzer.DiagnosticId));
    }

    private const string ValidDsaPublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIDRjCCAjkGByqGSM44BAEwggIsAoIBAQCp4KO7CJog+eAv5H8uTm47SbhQ0hLm\n" +
        "ffZwp0HwBrQz7AR/00DR2hl+V5cIj9znU84CQoZXw8AVekMZUFAvxGsk6dKO6bW9\n" +
        "/KyTaLjQc166BwQbPSryqNjmIZBbl/vzc4PJmViF7fosfay+D4pM2x+EtbwWbY8A\n" +
        "rOuKeb3etx1rwwKovwTMHrBXvdO/PgJ328oN401/wralzGIrcdVX9rnWZCoEPpYP\n" +
        "nMdH6tb/nyTHAwa4vIihOE7v1R/IcVv9/LB3RJRpQbQzmLG/uqxUtyvpxw4PEACO\n" +
        "pxR28UzaR2ArmIdplMSNiyJS4PAhq/BCebPnaPVT2kjwxLaxV0niVcpRAiEA4nw/\n" +
        "DjQ3kfoVJZWk8CvO3hRyFAnQcB99fxd+fHabTGsCggEASe4Bh6ZQA0w0T2C1tXP6\n" +
        "cMWKNpK3L5sz7SFKB6bHS7qpDDymyZdWFmRId0MT141+O8mMizjZfIk/EMHtplTC\n" +
        "2TQO5z6O64RsLa2ilRqShaEx6SQMt1rPzs2AJqKeb0pNEGBe9BbgxC/JfIsrYsI7\n" +
        "qvseILAGNqiYpaUpnNRD8ozsz2/cMV9pKDfNRMb2geEhLoqAvl1HWq2Zdpo7CZw8\n" +
        "KiDXVBxH37EIyrEoQJ4DGqt8dnpLRAAFEnBM+oHgf5luggQOTZp99FFaACktDHZB\n" +
        "hcpXDau84MBdS3ARd91m1SXA6OSfMxT87YDwjbIRO6LUFHhNE8xg8psXjeSw1O4O\n" +
        "dwOCAQUAAoIBAHrDjgyaZnljvAVNvVIuImB4ziQKiAIPdIK0LYjTaN7cvr0HyGMu\n" +
        "vxu7LEiJop2JHJPQkQcidBqD+b8xp499oC1vDmsjQX6qvzY3pYMDzLlhmfXNcvHM\n" +
        "3IIvor7cNRoNuImzCQRTP7yTbw48W+a7eHgzDcOjiWrr2cGWwd1L/c1m7HZre4r5\n" +
        "nfD76hwNI1kvRdoquwveIouY51340ajt2nYtizlVHlIcQYPTZF7WW9OkAvLYqYh0\n" +
        "+UO442YLmXS7TII5ox6kZc0utWT6tR95SauwqsPxl2hrrF7oDcPlvOQMmS1pMxJ2\n" +
        "GZrxXUsjKxVoXFyPGpwMu4VIMMWLzB5zfCg=\n" +
        "-----END PUBLIC KEY-----";

    [Fact]
    public async Task InvalidDsaPemLiteralAnalyzer_Should_NotFlag_ARealValidDsaPublicKeyPem()
    {
        var code = "using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\""
            + ValidDsaPublicKeyPem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidDsaPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidDsaPemLiteralAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task InvalidDsaPemLiteralAnalyzer_Should_Flag_ARsaKeyUnderAGenericLabel_WithCryptographicException()
    {
        var code = "using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\""
            + ValidRsaPublicKeyPem.Replace("\n", "\\n") + "\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidDsaPemLiteralAnalyzer(), code);
        var match = diagnostics.FirstOrDefault(d => d.Id == InvalidDsaPemLiteralAnalyzer.DiagnosticId);
        Assert.NotNull(match);
        Assert.Contains("always throws CryptographicException at runtime", match!.GetMessage(), StringComparison.Ordinal);
    }

    // ── Three hundred and thirty-fourth batch (411-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public void M() => new System.Guid(\"\"); }", true)]
    [InlineData("class C { public void M() => new System.Guid(\"not-a-guid\"); }", true)]
    [InlineData("class C { public void M() => new System.Guid(\"d3adb33f-0000-0000-0000-000000000000\"); }", false)]
    [InlineData("class C { public void M() => new System.Guid(\"d3adb33f000000000000000000000000\"); }", false)]
    [InlineData("class C { public void M() => new System.Guid(\"{d3adb33f-0000-0000-0000-000000000000}\"); }", false)]
    [InlineData("class C { public void M() => new System.Guid(\"(d3adb33f-0000-0000-0000-000000000000)\"); }", false)]
    [InlineData("class C { public void M() => new System.Guid(\"{0xd3adb33f,0x0000,0x0000,{0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00}}\"); }", false)]
    [InlineData("class C { public void M() => new System.Guid(\"  d3adb33f-0000-0000-0000-000000000000  \"); }", false)]
    [InlineData("class C { public void M(string x) => new System.Guid(x); }", false)]
    [InlineData("class C { public void M() { System.Guid g = new(\"not-a-guid\"); } }", true)]
    [InlineData("class C { public void M() => new System.Guid(new byte[16]); }", false)]
    [InlineData("class Guid { public Guid(string s) {} } class C { public void M() => new Guid(\"not-a-guid\"); }", false)]
    [InlineData("class C { public void M() => System.Guid.Parse(\"not-a-guid\"); }", false)]
    public async Task InvalidGuidConstructorLiteralAnalyzer_Should_FlagOnlyMalformedGuidConstructorLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidGuidConstructorLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidGuidConstructorLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirty-fifth batch (412-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa, string x) => rsa.ImportFromEncryptedPem(x, \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa, string pw) => rsa.ImportFromEncryptedPem(\"garbage\", pw); }", true)]
    [InlineData("class RSA { public void ImportFromEncryptedPem(string a, string b) {} } class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"\", \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\"garbage\"); }", false)]
    public async Task InvalidRsaEncryptedPemLiteralAnalyzer_Should_FlagOnlyMalformedInputLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRsaEncryptedPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRsaEncryptedPemLiteralAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task InvalidRsaEncryptedPemLiteralAnalyzer_Should_NotFlag_ARealValidEncryptedPem_EvenWithAWrongPassword()
    {
        using var rsa = RSA.Create(2048);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var encryptedPem = rsa.ExportEncryptedPkcs8PrivateKeyPem("correct-password", pbe);
        var code = "using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\""
            + encryptedPem.Replace("\n", "\\n") + "\", \"wrong-password\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidRsaEncryptedPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidRsaEncryptedPemLiteralAnalyzer.DiagnosticId);
    }

    // ── Three hundred and thirty-sixth batch (413-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromEncryptedPem(\"\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa, string x) => dsa.ImportFromEncryptedPem(x, \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa, string pw) => dsa.ImportFromEncryptedPem(\"garbage\", pw); }", true)]
    [InlineData("class DSA { public void ImportFromEncryptedPem(string a, string b) {} } class C { public void M(DSA dsa) => dsa.ImportFromEncryptedPem(\"\", \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\"garbage\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"garbage\", \"pw\"); }", false)]
    public async Task InvalidDsaEncryptedPemLiteralAnalyzer_Should_FlagOnlyMalformedInputLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDsaEncryptedPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDsaEncryptedPemLiteralAnalyzer.DiagnosticId));
    }

    // Nonsecret throwaway 1024/160-bit DSA key (OpenSSL pkcs8 -v2 aes-256-cbc, password "correct-password").
    // Fixed rather than generated at runtime: macOS can import DSA keys but cannot generate them, and only
    // accepts FIPS 186-2 sized (1024/160) private keys.
    private const string ValidDsaEncryptedPrivateKeyPem =
        "-----BEGIN ENCRYPTED PRIVATE KEY-----\n" +
        "MIIBnzBJBgkqhkiG9w0BBQ0wPDAbBgkqhkiG9w0BBQwwDgQIPtz3Ywb+42gCAggA\n" +
        "MB0GCWCGSAFlAwQBKgQQo6COcQBoaP90jIwYxNqrvQSCAVB1QMuE/2VkvpqVIY+6\n" +
        "XCwNQqoZWzAacGeIQoGUZoPV26UgwalJ1alH91lYA5pzhaDTo0nksiXVaCz6NVeB\n" +
        "tHV5L13bz9h3+mJTpH2H+ovJLEKGEAlIc/pyIOXQNGxfrk5KuJoNR7F62xHbiitY\n" +
        "zmhGuVb8YCoQm28V1EuCPTat6Swp7lyssG1C9CcghkveDCmV6yycInSIwcz5AVor\n" +
        "vQah83swtSV8MF529V6InYTmaoywyzwM8sLr/5baj7A95mV2GMR08UU6Ttda2wVf\n" +
        "8yidrECqzFx9WjZEiRostOSNuMvjTm0C1jWgjqA6bPD2FQxIiA5UP8aF03mS/Tjv\n" +
        "BX3FxVYAHuUs/sRp//Pguja90Wfjh7Lz1WfVeRxp7RXwThBry5+fUDy5+X9yM1JP\n" +
        "/VbW42RNqZn89CHsdDexiPOrrp76+iWQ9WysUnAOlW36QHY=\n" +
        "-----END ENCRYPTED PRIVATE KEY-----";

    [Fact]
    public async Task InvalidDsaEncryptedPemLiteralAnalyzer_Should_NotFlag_ARealValidEncryptedPem_EvenWithAWrongPassword()
    {
        using (var dsa = DSA.Create())
        {
            dsa.ImportFromEncryptedPem(ValidDsaEncryptedPrivateKeyPem, "correct-password");
        }
        using (var dsa = DSA.Create())
        {
            Assert.ThrowsAny<CryptographicException>(() => dsa.ImportFromEncryptedPem(ValidDsaEncryptedPrivateKeyPem, "wrong-password"));
        }
        var code = "using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromEncryptedPem(\""
            + ValidDsaEncryptedPrivateKeyPem.Replace("\n", "\\n") + "\", \"wrong-password\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidDsaEncryptedPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidDsaEncryptedPemLiteralAnalyzer.DiagnosticId);
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    // ── Three hundred and thirty-seventh batch (414-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromEncryptedPem(\"\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDiffieHellman ec) => ec.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec, string x) => ec.ImportFromEncryptedPem(x, \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec, string pw) => ec.ImportFromEncryptedPem(\"garbage\", pw); }", true)]
    [InlineData("class ECDsa { public void ImportFromEncryptedPem(string a, string b) {} } class C { public void M(ECDsa ec) => ec.ImportFromEncryptedPem(\"\", \"pw\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\"garbage\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"garbage\", \"pw\"); }", false)]
    public async Task InvalidEcAlgorithmEncryptedPemLiteralAnalyzer_Should_FlagOnlyMalformedInputLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmEncryptedPemLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEcAlgorithmEncryptedPemLiteralAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task InvalidEcAlgorithmEncryptedPemLiteralAnalyzer_Should_NotFlag_ARealValidEncryptedPem_EvenWithAWrongPassword()
    {
        using var ecdsa = ECDsa.Create();
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var encryptedPem = ecdsa.ExportEncryptedPkcs8PrivateKeyPem("correct-password", pbe);
        var code = "using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromEncryptedPem(\""
            + encryptedPem.Replace("\n", "\\n") + "\", \"wrong-password\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmEncryptedPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidEcAlgorithmEncryptedPemLiteralAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task InvalidEcAlgorithmEncryptedPemLiteralAnalyzer_Should_NotFlag_ARealValidEncryptedPem_ViaECDiffieHellman()
    {
        using var ecdsa = ECDsa.Create();
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var encryptedPem = ecdsa.ExportEncryptedPkcs8PrivateKeyPem("correct-password", pbe);
        var code = "using System.Security.Cryptography; class C { public void M(ECDiffieHellman ec) => ec.ImportFromEncryptedPem(\""
            + encryptedPem.Replace("\n", "\\n") + "\", \"correct-password\"); }";
        var diagnostics = await GetDiagnosticsAsync(new InvalidEcAlgorithmEncryptedPemLiteralAnalyzer(), code);
        Assert.DoesNotContain(diagnostics, d => d.Id == InvalidEcAlgorithmEncryptedPemLiteralAnalyzer.DiagnosticId);
    }

    // ── Three hundred and thirty-eighth batch (415-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"\"); }", false)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"not base64url!!\"); }", true)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"SGVsbG8\"); }", false)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"ab+/c\"); }", true)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"SGVsbG8=\"); }", false)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"a\"); }", true)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"SGVs bG8\"); }", false)]
    [InlineData("using System.Buffers.Text; class C { public byte[] M(string x) => Base64Url.DecodeFromChars(x); }", false)]
    [InlineData("using System.Buffers.Text; class C { public int M(System.Span<byte> dest) => Base64Url.DecodeFromChars(\"not base64url!!\", dest); }", false)]
    [InlineData("class Base64Url { public static byte[] DecodeFromChars(string s) => null; } class C { public byte[] M() => Base64Url.DecodeFromChars(\"not base64url!!\"); }", false)]
    [InlineData("class C { public byte[] M() => System.Convert.FromBase64String(\"not base64!!\"); }", false)]
    public async Task InvalidBase64UrlLiteralAnalyzer_Should_FlagOnlyMalformedUrlSafeBase64Literals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBase64UrlLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBase64UrlLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and thirty-ninth batch (416-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"\", null, null, null); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"1bad\", null, null, null); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"bad name\", null, null, null); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"html\", null, null, null); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"ns:html\", null, null, null); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M(string n) => new XDocumentType(n, null, null, null); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"html\", \"not valid!!\", null, null); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"html\", null, \"not valid<>\", null); }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M() { XDocumentType d = new(\"1bad\", null, null, null); } }", true)]
    [InlineData("class XDocumentType { public XDocumentType(string a, string b, string c, string d) {} } class C { public void M() => new XDocumentType(\"1bad\", null, null, null); }", false)]
    public async Task InvalidXDocumentTypeNameLiteralAnalyzer_Should_FlagOnlyInvalidXmlNames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXDocumentTypeNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXDocumentTypeNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fortieth batch (417-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"not a pem\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"-----BEGIN FOO-----\\nAAAA\\n-----END FOO-----\"); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"-----BEGIN FOO-----\\nAAAA\\n-----END BAR-----\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"-----BEGIN FOO-----\\n!!!!\\n-----END FOO-----\"); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M(string x) => PemEncoding.Find(x); }", false)]
    [InlineData("class PemEncoding { public static void Find(string s) {} } class C { public void M() => PemEncoding.Find(\"\"); }", false)]
    public async Task InvalidPemEncodingFindLiteralAnalyzer_Should_FlagOnlyMalformedPemLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPemEncodingFindLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPemEncodingFindLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-first batch (418-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\" \"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"notabool\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"true\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"False\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"1\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"2\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlBoolean M(string x) => SqlBoolean.Parse(x); }", false)]
    [InlineData("class SqlBoolean { public static SqlBoolean Parse(string s) => default; } class C { public void M() => SqlBoolean.Parse(\"\"); }", false)]
    [InlineData("class C { public bool M() => bool.Parse(\"notabool\"); }", false)]
    public async Task InvalidSqlBooleanParseLiteralAnalyzer_Should_FlagOnlyUnrecognizedBooleanLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlBooleanParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlBooleanParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-second batch (419-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\" \"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"\\t\\n\\r \"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"a\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"   a\"); }", true)]
    [InlineData("using System.Xml; class C { public string M(string x) => XmlConvert.VerifyWhitespace(x); }", false)]
    [InlineData("class XmlConvert { public static string VerifyWhitespace(string s) => s; } class C { public void M() => XmlConvert.VerifyWhitespace(\"a\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyName(\"1bad\"); }", false)]
    public async Task InvalidXmlWhitespaceLiteralAnalyzer_Should_FlagOnlyNonWhitespaceContainingLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlWhitespaceLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlWhitespaceLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-third batch (420-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"hello\\nworld\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"-//W3C//DTD HTML 4.01//EN\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"<bad>\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"bad{brace}\"); }", true)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"hello\\tworld\"); }", true)]
    [InlineData("using System.Xml; class C { public string M(string x) => XmlConvert.VerifyPublicId(x); }", false)]
    [InlineData("class XmlConvert { public static string VerifyPublicId(string s) => s; } class C { public void M() => XmlConvert.VerifyPublicId(\"<bad>\"); }", false)]
    [InlineData("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"a\"); }", false)]
    public async Task InvalidXmlPublicIdLiteralAnalyzer_Should_FlagOnlyLiteralsWithDisallowedCharacters(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlPublicIdLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlPublicIdLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-fourth batch (421-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(\"not-a-guid\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(\"d3adb33f-0000-0000-0000-000000000000\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(\"{0xd3adb33f,0x0000,0x0000,{0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00}}\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => SqlGuid.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => SqlGuid.Parse(\"not-a-guid\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => SqlGuid.Parse(\"d3adb33f-0000-0000-0000-000000000000\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(new byte[16]); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M() => new SqlGuid(System.Guid.Empty); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlGuid M(string x) => new SqlGuid(x); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public void M() { SqlGuid g = new(\"not-a-guid\"); } }", true)]
    [InlineData("class SqlGuid { public SqlGuid(string s) {} } class C { public void M() => new SqlGuid(\"not-a-guid\"); }", false)]
    [InlineData("class C { public void M() => System.Guid.Parse(\"not-a-guid\"); }", false)]
    public async Task InvalidSqlGuidLiteralAnalyzer_Should_FlagOnlyMalformedSqlGuidLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlGuidLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlGuidLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-fifth batch (422-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public System.Guid M() => XmlConvert.ToGuid(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public System.Guid M() => XmlConvert.ToGuid(\"not-a-guid\"); }", true)]
    [InlineData("using System.Xml; class C { public System.Guid M() => XmlConvert.ToGuid(\"d3adb33f-0000-0000-0000-000000000000\"); }", false)]
    [InlineData("using System.Xml; class C { public System.Guid M() => XmlConvert.ToGuid(\"{0xd3adb33f,0x0000,0x0000,{0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00}}\"); }", false)]
    [InlineData("using System.Xml; class C { public System.Guid M(string x) => XmlConvert.ToGuid(x); }", false)]
    [InlineData("class XmlConvert { public static System.Guid ToGuid(string s) => default; } class C { public void M() => XmlConvert.ToGuid(\"not-a-guid\"); }", false)]
    [InlineData("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"not-a-guid\"); }", false)]
    [InlineData("class C { public void M() => System.Guid.Parse(\"not-a-guid\"); }", false)]
    public async Task InvalidXmlGuidLiteralAnalyzer_Should_FlagOnlyMalformedGuidLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlGuidLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlGuidLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-sixth batch (423-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"\", XmlDateTimeSerializationMode.RoundtripKind); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"not-a-date\", XmlDateTimeSerializationMode.RoundtripKind); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"2024-01-01T00:00:00\", XmlDateTimeSerializationMode.RoundtripKind); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"2024-13-01T00:00:00\", XmlDateTimeSerializationMode.RoundtripKind); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"01/01/2024\", XmlDateTimeSerializationMode.RoundtripKind); }", true)]
    [InlineData("using System.Xml; class C { public System.DateTime M(string x) => XmlConvert.ToDateTime(x, XmlDateTimeSerializationMode.RoundtripKind); }", false)]
    [InlineData("class XmlConvert { public static System.DateTime ToDateTime(string s, int m) => default; } class C { public void M() => XmlConvert.ToDateTime(\"not-a-date\", 0); }", false)]
    [InlineData("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"not-a-date\"); }", false)]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"01/01/2024\"); }", false)]
    public async Task InvalidXmlDateTimeLiteralAnalyzer_Should_FlagOnlyInvalidXsdDateTimeLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlDateTimeLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlDateTimeLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-seventh batch (425-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"not xml\"); }", true)]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"<root><child></root>\"); }", true)]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"<a/><b/>\"); }", true)]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"<foo:root/>\"); }", true)]
    [InlineData("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"<root/>\"); }", false)]
    [InlineData("using System.Xml; class C { public void M() { var d = new XmlDocument(); d.LoadXml(\"not xml\"); } }", true)]
    [InlineData("using System.Xml; class C { public void M(XmlDocument d, string x) => d.LoadXml(x); }", false)]
    [InlineData("class XmlDocument { public void LoadXml(string s) {} } class C { public void M() => new XmlDocument().LoadXml(\"not xml\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M() => XDocument.Parse(\"not xml\"); }", false)]
    public async Task InvalidXmlDocumentLoadXmlLiteralAnalyzer_Should_FlagOnlyMalformedXmlLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlDocumentLoadXmlLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlDocumentLoadXmlLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-eighth batch (426-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Diagnostics; class C { public ActivityTraceId M() => ActivityTraceId.CreateFromString(\"abc\"); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityTraceId M() => ActivityTraceId.CreateFromString(\"0af7651916cd43dd8448eb211c80319c\"); }", false)]
    [InlineData("using System.Diagnostics; class C { public ActivityTraceId M() => ActivityTraceId.CreateFromString(\"0AF7651916CD43DD8448EB211C80319C\"); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityTraceId M() => ActivityTraceId.CreateFromString(\"00000000000000000000000000000000\"); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivitySpanId M() => ActivitySpanId.CreateFromString(\"abc\"); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivitySpanId M() => ActivitySpanId.CreateFromString(\"b7ad6b7169203331\"); }", false)]
    [InlineData("using System.Diagnostics; class C { public ActivitySpanId M() => ActivitySpanId.CreateFromString(\"0000000000000000\"); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityTraceId M(string x) => ActivityTraceId.CreateFromString(x); }", false)]
    [InlineData("class ActivityTraceId { public static ActivityTraceId CreateFromString(string s) => default; } class C { public void M() => ActivityTraceId.CreateFromString(\"abc\"); }", false)]
    public async Task InvalidActivityIdLiteralAnalyzer_Should_FlagOnlyMalformedActivityIds(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidActivityIdLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidActivityIdLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and forty-ninth batch (427-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FromSerializedString(\"\"); }", true)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FromSerializedString(\"abc123\"); }", true)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FromSerializedString(\"   \"); }", true)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FromSerializedString(\"UTC;0;(UTC) Coordinated Universal Time;Coordinated Universal Time;Coordinated Universal Time;;\"); }", false)]
    [InlineData("class C { public System.TimeZoneInfo M(string x) => System.TimeZoneInfo.FromSerializedString(x); }", false)]
    [InlineData("class TimeZoneInfo { public static TimeZoneInfo FromSerializedString(string s) => default; } class C { public void M() => TimeZoneInfo.FromSerializedString(\"\"); }", false)]
    [InlineData("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FindSystemTimeZoneById(\"\"); }", false)]
    public async Task InvalidTimeZoneInfoSerializedStringLiteralAnalyzer_Should_FlagOnlyMalformedSerializedStrings(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimeZoneInfoSerializedStringLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimeZoneInfoSerializedStringLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fiftieth batch (428-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"\", true); }", true)]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"System.String[[[\", true); }", true)]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"System.String\", true); }", false)]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"Some.Totally.Fictional.TypeName123\", true); }", false)]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"\", false); }", false)]
    [InlineData("class C { public System.Type M(bool b) => System.Type.GetType(\"\", b); }", false)]
    [InlineData("class C { public System.Type M() => System.Type.GetType(\"System.String, mscorlib\", true); }", false)]
    [InlineData("class C { public System.Type M(string x) => System.Type.GetType(x, true); }", false)]
    [InlineData("class Type { public static Type GetType(string s, bool b) => default; } class C { public void M() => Type.GetType(\"\", true); }", false)]
    public async Task InvalidTypeGetTypeLiteralAnalyzer_Should_FlagOnlyEmptyOrMalformedGenericSyntax(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTypeGetTypeLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTypeGetTypeLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-first batch (429-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.Version M() => new System.Version(\"\"); }", true)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1\"); }", true)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1.x\"); }", true)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1.2.3.4.5\"); }", true)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1.-2\"); }", true)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1.2\"); }", false)]
    [InlineData("class C { public System.Version M() => new System.Version(\"1.2.3.4\"); }", false)]
    [InlineData("class C { public System.Version M(string x) => new System.Version(x); }", false)]
    [InlineData("class C { public void M() { System.Version v = new(\"1.x\"); } }", true)]
    [InlineData("class Version { public Version(string s) {} } class C { public void M() => new Version(\"1.x\"); }", false)]
    [InlineData("class C { public System.Version M() => System.Version.Parse(\"1.x\"); }", false)]
    public async Task InvalidVersionConstructorLiteralAnalyzer_Should_FlagOnlyMalformedVersionLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidVersionConstructorLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidVersionConstructorLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-second batch (430-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"\", null); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"not a traceparent\", null); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\", null); }", false)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"00-4BF92F3577B34DA6A3CE929D0E0E4736-00F067AA0BA902B7-01\", null); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"00-00000000000000000000000000000000-00f067aa0ba902b7-01\", null); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\", null); }", true)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\", \"!!!not valid!!!\"); }", false)]
    [InlineData("using System.Diagnostics; class C { public ActivityContext M(string x) => ActivityContext.Parse(x, null); }", false)]
    [InlineData("class ActivityContext { public static ActivityContext Parse(string a, string b) => default; } class C { public void M() => ActivityContext.Parse(\"\", null); }", false)]
    public async Task InvalidActivityContextTraceParentLiteralAnalyzer_Should_FlagOnlyMalformedTraceParents(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidActivityContextTraceParentLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidActivityContextTraceParentLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-third batch (431-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"\"); }", true)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"http://bad host/\"); }", true)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"   \"); }", true)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"http://example.com:abc/\"); }", true)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"http://ex:ample:80/\"); }", true)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"http://example.com\"); }", false)]
    [InlineData("class C { public System.UriBuilder M() => new System.UriBuilder(\"example.com\"); }", false)]
    [InlineData("class C { public System.UriBuilder M(string x) => new System.UriBuilder(x); }", false)]
    [InlineData("class C { public void M() { System.UriBuilder u = new(\"http://bad host/\"); } }", true)]
    [InlineData("class C { public System.Uri M() => new System.Uri(\"http://bad host/\"); }", false)]
    [InlineData("class UriBuilder { public UriBuilder(string s) {} } class C { public void M() => new UriBuilder(\"\"); }", false)]
    public async Task InvalidUriBuilderLiteralAnalyzer_Should_FlagOnlyMalformedUris(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidUriBuilderLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidUriBuilderLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-fourth batch (432-й проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public string M() => \"abc\\uD800def\".Normalize(); }", true)]
    [InlineData("class C { public string M() => \"abc\\uDC00def\".Normalize(); }", true)]
    [InlineData("class C { public string M() => \"hello\".Normalize(); }", false)]
    [InlineData("class C { public string M() => \"\".Normalize(); }", false)]
    [InlineData("using System.Text; class C { public string M() => \"abc\\uD800def\".Normalize(NormalizationForm.FormKC); }", true)]
    [InlineData("class C { public bool M() => \"abc\\uD800def\".IsNormalized(); }", true)]
    [InlineData("class C { public string M(string x) => x.Normalize(); }", false)]
    [InlineData("class MyString { public string Normalize() => null; } class C { public string M(MyString s) => s.Normalize(); }", false)]
    [InlineData("class C { public string M() => (\"abc\" + \"\\uD800\" + \"def\").Normalize(); }", true)]
    public async Task InvalidStringNormalizeLiteralAnalyzer_Should_FlagOnlyUnpairedSurrogateLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringNormalizeLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringNormalizeLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-fifth batch (433-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.IO; class C { public string M() => Path.GetFullPath(\"abc\\0def\"); }", true)]
    [InlineData("using System.IO; class C { public FileInfo M() => new FileInfo(\"abc\\0def\"); }", true)]
    [InlineData("using System.IO; class C { public DirectoryInfo M() => new DirectoryInfo(\"abc\\0def\"); }", true)]
    [InlineData("using System.IO; class C { public DirectoryInfo M() => Directory.CreateDirectory(\"abc\\0def\"); }", true)]
    [InlineData("using System.IO; class C { public string M() => Path.GetFullPath(\"not/a/real/path\"); }", false)]
    [InlineData("using System.IO; class C { public string M() => Path.Combine(\"a\", \"abc\\0def\"); }", false)]
    [InlineData("using System.IO; class C { public bool M() => File.Exists(\"abc\\0def\"); }", false)]
    [InlineData("using System.IO; class C { public string M() => Path.GetDirectoryName(\"abc\\0def\"); }", false)]
    [InlineData("using System.IO; class C { public string M(string x) => Path.GetFullPath(x); }", false)]
    [InlineData("class Path { public static string GetFullPath(string s) => s; } class C { public void M() => Path.GetFullPath(\"abc\\0def\"); }", false)]
    public async Task InvalidPathEmbeddedNulLiteralAnalyzer_Should_FlagOnlyEmbeddedNulLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPathEmbeddedNulLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPathEmbeddedNulLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-sixth batch (434-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public char M() => XmlConvert.ToChar(\"\"); }", true)]
    [InlineData("using System.Xml; class C { public char M() => XmlConvert.ToChar(\"a\"); }", false)]
    [InlineData("using System.Xml; class C { public char M() => XmlConvert.ToChar(\"ab\"); }", true)]
    [InlineData("using System.Xml; class C { public char M(string x) => XmlConvert.ToChar(x); }", false)]
    [InlineData("class XmlConvert { public static char ToChar(string s) => 'x'; } class C { public void M() => XmlConvert.ToChar(\"\"); }", false)]
    [InlineData("class C { public char M() => System.Convert.ToChar(\"ab\"); }", false)]
    public async Task InvalidXmlCharLiteralAnalyzer_Should_FlagOnlyLiteralsNotExactlyOneCharacter(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlCharLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlCharLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-seventh batch (435-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml; class C { public int M() => XmlConvert.ToInt32(\"abc\"); }", true)]
    [InlineData("using System.Xml; class C { public int M() => XmlConvert.ToInt32(\"99999999999999999999\"); }", true)]
    [InlineData("using System.Xml; class C { public int M() => XmlConvert.ToInt32(\"  123  \"); }", false)]
    [InlineData("using System.Xml; class C { public int M() => XmlConvert.ToInt32(\"+123\"); }", false)]
    [InlineData("using System.Xml; class C { public double M() => XmlConvert.ToDouble(\"INF\"); }", false)]
    [InlineData("using System.Xml; class C { public double M() => XmlConvert.ToDouble(\"NaN\"); }", false)]
    [InlineData("using System.Xml; class C { public byte M() => XmlConvert.ToByte(\"999\"); }", true)]
    [InlineData("using System.Xml; class C { public decimal M() => XmlConvert.ToDecimal(\"abc\"); }", true)]
    [InlineData("using System.Xml; class C { public int M(string x) => XmlConvert.ToInt32(x); }", false)]
    [InlineData("class XmlConvert { public static int ToInt32(string s) => 0; } class C { public void M() => XmlConvert.ToInt32(\"abc\"); }", false)]
    [InlineData("class C { public int M() => int.Parse(\"abc\"); }", false)]
    [InlineData("using System.Xml; class C { public char M() => XmlConvert.ToChar(\"ab\"); }", false)]
    public async Task InvalidXmlNumericLiteralAnalyzer_Should_FlagOnlyInvalidXsdNumericLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlNumericLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlNumericLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-eighth batch (436-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"bogus\"), d); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"\"), d); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"SHA256\"), d); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"sha256\"), d); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public IncrementalHash M() => IncrementalHash.CreateHash(new HashAlgorithmName(\"bogus\")); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public IncrementalHash M(byte[] k) => IncrementalHash.CreateHMAC(new HashAlgorithmName(\"bogus\"), k); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] k, byte[] d) => CryptographicOperations.HmacData(new HashAlgorithmName(\"bogus\"), k, d); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(string x, byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(x), d); }", false)]
    [InlineData("class CryptographicOperations { public static byte[] HashData(object a, byte[] d) => d; } class HashAlgorithmName { public HashAlgorithmName(string s) {} } class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"bogus\"), d); }", false)]
    public async Task InvalidHashAlgorithmNameLiteralAnalyzer_Should_FlagOnlyUnknownAlgorithmNames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHashAlgorithmNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHashAlgorithmNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fifty-ninth batch (437-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"2024/01/01\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"2024-01-01\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"Januar 01 2024\", \"MMMM dd yyyy\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"Januar 01 2024\", \"MMMM dd yyyy\", CultureInfo.GetCultureInfo(\"de-DE\")); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", CultureInfo.CurrentCulture); }", false)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(IFormatProvider p) => DateTime.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", p); }", false)]
    [InlineData("using System; using System.Globalization; class C { public TimeSpan M() => TimeSpan.ParseExact(\"not-a-timespan\", @\"hh\\:mm\\:ss\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateOnly M() => DateOnly.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public TimeOnly M() => TimeOnly.ParseExact(\"not-a-time\", \"HH:mm:ss\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTimeOffset M() => DateTimeOffset.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", true)]
    [InlineData("using System; using System.Globalization; class C { public DateTime M(string x) => DateTime.ParseExact(x, \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }", false)]
    [InlineData("using System; class C { public DateTime M() => DateTime.Parse(\"not-a-date\"); }", false)]
    [InlineData("class DateTime { public static DateTime ParseExact(string a, string b, object c) => default; } class C { public void M() => DateTime.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", null); }", false)]
    public async Task InvalidDateOrTimeParseExactLiteralAnalyzer_Should_FlagOnlyInvariantCultureMismatches(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateOrTimeParseExactLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateOrTimeParseExactLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixty-third batch (441-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public XElement M() => new XElement(\"\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XElement M() => new XElement(\"bad name\", \"v\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XElement M() => new XElement(\"\", \"a\", \"b\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XAttribute M() => new XAttribute(\"\", \"v\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XAttribute M() => new XAttribute(\"1bad\", \"v\"); }", true)]
    [InlineData("using System.Xml.Linq; class C { public XElement M() => new XElement(\"valid\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XAttribute M() => new XAttribute(\"valid\", \"v\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XAttribute M() => new XAttribute(\"{urn:x}local\", \"v\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XElement M(string x) => new XElement(x); }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M() { XElement e = new(\"\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public XName M() => XName.Get(\"\"); }", false)]
    [InlineData("class XElement { public XElement(string s) {} } class C { public void M() => new XElement(\"\"); }", false)]
    [InlineData("using System.Xml.Linq; class C { public XDocument M(XElement e) => new XDocument(e); }", false)]
    public async Task InvalidXElementOrXAttributeNameLiteralAnalyzer_Should_FlagOnlyInvalidExpandedNames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXElementOrXAttributeNameLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXElementOrXAttributeNameLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixty-fourth batch (442-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"1+\"; } }", true)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"1+1\"; } }", false)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"\"; } }", false)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"'abc\"; } }", true)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"#notadate#\"; } }", false)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"Y + 1\"; } }", false)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); t.Select(\"1+\"); } }", true)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); t.Select(\"1=1\"); } }", false)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); t.Compute(\"SUM(\", \"\"); } }", true)]
    [InlineData("using System.Data; class C { public void M() { var t = new DataTable(); t.Compute(\"COUNT(E)\", \"\"); } }", false)]
    [InlineData("using System.Data; class C { public void M(string x) { var t = new DataTable(); t.Select(x); } }", false)]
    [InlineData("class DataTable { public void Select(string s) {} } class C { public void M() => new DataTable().Select(\"1+\"); }", false)]
    [InlineData("using System.Data; class C { class Foo { public string Expression { get; set; } } public void M() { var f = new Foo(); f.Expression = \"1+\"; } }", false)]
    public async Task InvalidDataExpressionLiteralAnalyzer_Should_FlagOnlyMalformedExpressions(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDataExpressionLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDataExpressionLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"\", \"/x\"); }", true)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"/a\", \"\"); }", true)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"\", \"\"); }", true)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\" \", \"/x\"); }", false)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"/a\", \" \"); }", false)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"/a\", \"/a/b\"); }", false)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"/a\\0b\", \"/x\"); }", true)]
    [InlineData("using System.IO; class C { public string M() => Path.GetRelativePath(\"/a\", \"/x\\0y\"); }", true)]
    [InlineData("using System.IO; class C { public string M(string r) => Path.GetRelativePath(r, \"\"); }", true)]
    [InlineData("using System.IO; class C { public string M(string r, string p) => Path.GetRelativePath(r, p); }", false)]
    [InlineData("class Path { public static string GetRelativePath(string a, string b) => \"\"; } class C { public void M() => Path.GetRelativePath(\"\", \"\"); }", false)]
    public async Task InvalidPathGetRelativePathLiteralAnalyzer_Should_FlagOnlyEmptyOrEmbeddedNulArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPathGetRelativePathLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPathGetRelativePathLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixty-sixth and sixty-seventh batches (443-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\" \"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"Foo, Version=bad\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"Foo, Version=1.2.3.4.5\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"Foo, PublicKeyToken=abc\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"Foo, PublicKeyToken=zzzzzzzzzzzzzzzz\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"System.Private.CoreLib\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"A, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"A, Culture=xx-XX-XX-not-a-culture\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"Foo,,Bar\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"TotallyMadeUpAssembly\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public AssemblyNameInfo M(string x) => AssemblyNameInfo.Parse(x); }", false)]
    [InlineData("class AssemblyNameInfo { public static AssemblyNameInfo Parse(string s) => new(); } class C { public void M() => AssemblyNameInfo.Parse(\"\"); }", false)]
    public async Task InvalidAssemblyNameInfoLiteralAnalyzer_Should_FlagOnlyInvalidAssemblyDisplayNames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAssemblyNameInfoLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidAssemblyNameInfoLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\" \"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32[\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32]\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"[System.Int32\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"&System.Int32\"); }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32[]\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32*\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System..Int32\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32 ! garbage\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Collections.Generic.Dictionary`2[[System.String]]\"); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"[\", default); }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public TypeName M(string x) => TypeName.Parse(x); }", false)]
    [InlineData("class TypeName { public static TypeName Parse(string s) => new(); } class C { public void M() => TypeName.Parse(\"\"); }", false)]
    public async Task InvalidTypeNameParseLiteralAnalyzer_Should_FlagOnlyMalformedTypeNames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTypeNameParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTypeNameParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixty-eighth batch (443-й проход, own pick) — one new analyzer.

    [Theory]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"January\"); }", true)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"today\"); }", true)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"\"); }", true)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\" \"); }", true)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"Z\"); }", true)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"2024-01-01\"); }", false)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime(\"9\"); }", false)]
    [InlineData("using System; class C { public DateTime M() => Convert.ToDateTime((string)null); }", false)]
    [InlineData("using System; class C { public DateTime M(string x) => Convert.ToDateTime(x); }", false)]
    [InlineData("class Convert { public static object ToDateTime(string s) => null; } class C { public void M() => Convert.ToDateTime(\"today\"); }", false)]
    [InlineData("using System; class C { public int M() => Convert.ToInt32(\"today\"); }", false)]
    public async Task InvalidConvertToDateTimeLiteralAnalyzer_Should_FlagOnlyNoDigitLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidConvertToDateTimeLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidConvertToDateTimeLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and sixty-ninth and seventieth batches (445-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Data.SqlTypes; class C { public SqlByte M() => SqlByte.Parse(\"abc\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt16 M() => SqlInt16.Parse(\"1.5\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt32 M() => SqlInt32.Parse(\"\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt64 M() => SqlInt64.Parse(\"NaN\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlByte M() => SqlByte.Parse(\"999999999999999999999999999999\"); }", true)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt32 M() => SqlInt32.Parse(\"123\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt32 M() => SqlInt32.Parse(\"-5\"); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlInt32 M(string x) => SqlInt32.Parse(x); }", false)]
    [InlineData("using System.Data.SqlTypes; class C { public SqlDouble M() => SqlDouble.Parse(\"abc\"); }", false)]
    [InlineData("class SqlInt32 { public static SqlInt32 Parse(string s) => default; } class C { public void M() => SqlInt32.Parse(\"abc\"); }", false)]
    public async Task InvalidSqlIntegerParseLiteralAnalyzer_Should_FlagOnlyMalformedOrOverflowingLiterals(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSqlIntegerParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSqlIntegerParseLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"\"); }", true)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"not a url with spaces\"); }", true)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"://bad\"); }", true)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"ht!tp://x\"); }", true)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"http://\"); }", true)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"http://proxy:8080\"); }", false)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"127.0.0.1:8080\"); }", false)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"hello\"); }", false)]
    [InlineData("using System.Net; class C { public WebProxy M(string x) => new WebProxy(x); }", false)]
    [InlineData("using System.Net; class C { public WebProxy M() => new WebProxy(\"\", true); }", false)]
    [InlineData("class WebProxy { public WebProxy(string s) {} } class C { public void M() => new WebProxy(\"\"); }", false)]
    public async Task InvalidWebProxyLiteralAnalyzer_Should_FlagOnlyMalformedUris(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidWebProxyLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidWebProxyLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and seventy-second batch (446-й проход) — one new analyzer (plus RPAD0346 extension above).

    [Theory]
    [InlineData("using System; using System.Xml; class C { public string M(DateTime d) => XmlConvert.ToString(d, \"Z\"); }", true)]
    [InlineData("using System; using System.Xml; class C { public string M(DateTime d) => XmlConvert.ToString(d, \"s\"); }", false)]
    [InlineData("using System; using System.Xml; class C { public string M(DateTimeOffset d) => XmlConvert.ToString(d, \"Z\"); }", true)]
    [InlineData("using System; using System.Xml; class C { public string M(DateTimeOffset d) => XmlConvert.ToString(d, \"s\"); }", false)]
    [InlineData("using System; using System.Xml; class C { public string M(DateTime d, string f) => XmlConvert.ToString(d, f); }", false)]
    [InlineData("using System.Xml; class C { public string M(int i) => XmlConvert.ToString(i); }", false)]
    [InlineData("class XmlConvert { public static string ToString(int a, string b) => \"\"; } class C { public void M() => XmlConvert.ToString(1, \"Z\"); }", false)]
    public async Task InvalidXmlConvertToStringFormatAnalyzer_Should_FlagOnlyInvalidFormatSpecifiers(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlConvertToStringFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlConvertToStringFormatAnalyzer.DiagnosticId));
    }

    // ── Three hundred and seventy-third and seventy-fourth batches (448-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(14), \"d\", \"s\"); }", false)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(15), \"d\", \"s\"); }", true)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(-15), \"d\", \"s\"); }", true)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromSeconds(3661), \"d\", \"s\"); }", true)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.Zero, \"d\", \"s\"); }", false)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromMinutes(30), \"d\", \"s\"); }", false)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(15), \"d\", \"s\", \"f\", null); }", true)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(15), \"d\", \"s\", \"f\", null, true); }", true)]
    [InlineData("using System; class C { public TimeZoneInfo M(TimeSpan t) => TimeZoneInfo.CreateCustomTimeZone(\"id\", t, \"d\", \"s\"); }", false)]
    [InlineData("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.FindSystemTimeZoneById(\"x\"); }", false)]
    public async Task InvalidTimeZoneCustomOffsetAnalyzer_Should_FlagOnlyOutOfRangeOffsets(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTimeZoneCustomOffsetAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTimeZoneCustomOffsetAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyPositivePattern = 4; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyPositivePattern = 3; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyNegativePattern = 17; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyNegativePattern = 16; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberDecimalDigits = 100; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberDecimalDigits = -1; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().PercentNegativePattern = 12; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberNegativePattern = 4; } }", false)]
    [InlineData("using System.Globalization; class C { public void M(int v) { new NumberFormatInfo().CurrencyPositivePattern = v; } }", false)]
    [InlineData("class NumberFormatInfo { public int CurrencyPositivePattern { get; set; } } class C { public void M() { new NumberFormatInfo().CurrencyPositivePattern = 4; } }", false)]
    [InlineData("using System.Globalization; class C { class Foo { public int CurrencyPositivePattern { get; set; } } public void M() { new Foo().CurrencyPositivePattern = 4; } }", false)]
    public async Task InvalidNumberFormatInfoPatternAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidNumberFormatInfoPatternAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidNumberFormatInfoPatternAnalyzer.DiagnosticId));
    }

    // ── Three hundred and seventy-fifth batch (449-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { 3, 0, 2 }; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { 3, 0 }; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { -1 }; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new int[] { }; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { 9, 9, 9 }; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { 10 }; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyGroupSizes = new[] { 3, 0, 2 }; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().PercentGroupSizes = new[] { 0 }; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = [3, 0, 2]; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = [3, 3]; } }", false)]
    [InlineData("using System.Globalization; class C { public void M(int x) { new NumberFormatInfo().NumberGroupSizes = new[] { 3, x }; } }", false)]
    [InlineData("using System.Globalization; class C { public void M(int[] a) { new NumberFormatInfo().NumberGroupSizes = a; } }", false)]
    [InlineData("class NumberFormatInfo { public int[] NumberGroupSizes { get; set; } } class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { -1 }; } }", false)]
    [InlineData("using System.Globalization; class C { class Foo { public int[] NumberGroupSizes { get; set; } } public void M() { new Foo().NumberGroupSizes = new[] { -1 }; } }", false)]
    public async Task InvalidNumberFormatInfoGroupSizesAnalyzer_Should_FlagOnlyInvalidGroupSizeArrays(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidNumberFormatInfoGroupSizesAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidNumberFormatInfoGroupSizesAnalyzer.DiagnosticId));
    }

    // ── Three hundred seventy-sixth through eighty-first batches (450-й проход) — five new analyzers
    // (plus the NegativeListCapacityAssignmentAnalyzer/RPAD0293 extension added above).

    [Theory]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().Calendar = new HijriCalendar(); } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().Calendar = new ThaiBuddhistCalendar(); } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().Calendar = new GregorianCalendar(); } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().Calendar = new GregorianCalendar(GregorianCalendarTypes.Localized); } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().Calendar = new GregorianCalendar(GregorianCalendarTypes.USEnglish); } }", true)]
    [InlineData("using System.Globalization; class C { public void M(Calendar c) { new DateTimeFormatInfo().Calendar = c; } }", false)]
    [InlineData("class DateTimeFormatInfo { public object Calendar { get; set; } } class C { public void M() { new DateTimeFormatInfo().Calendar = new object(); } }", false)]
    public async Task InvalidDateTimeFormatInfoCalendarAssignmentAnalyzer_Should_FlagOnlyIncompatibleCalendars(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateTimeFormatInfoCalendarAssignmentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateTimeFormatInfoCalendarAssignmentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; using System.Globalization; class C { public void M() { new DateTimeFormatInfo().FirstDayOfWeek = (DayOfWeek)7; } }", true)]
    [InlineData("using System; using System.Globalization; class C { public void M() { new DateTimeFormatInfo().FirstDayOfWeek = (DayOfWeek)(-1); } }", true)]
    [InlineData("using System; using System.Globalization; class C { public void M() { new DateTimeFormatInfo().FirstDayOfWeek = (DayOfWeek)3; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().CalendarWeekRule = (CalendarWeekRule)3; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { new DateTimeFormatInfo().CalendarWeekRule = (CalendarWeekRule)0; } }", false)]
    [InlineData("using System; using System.Globalization; class C { public void M(DayOfWeek d) { new DateTimeFormatInfo().FirstDayOfWeek = d; } }", false)]
    public async Task InvalidDateTimeFormatInfoEnumPropertyAnalyzer_Should_FlagOnlyOutOfRangeEnumValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateTimeFormatInfoEnumPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateTimeFormatInfoEnumPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().MaxDepth = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().MaxDepth = 0; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().DefaultBufferSize = 0; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().DefaultBufferSize = 16; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().IndentSize = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().IndentSize = 128; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().IndentSize = 127; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().IndentCharacter = 'x'; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().IndentCharacter = ' '; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M(int v) { new JsonSerializerOptions().MaxDepth = v; } }", false)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().NumberHandling = (JsonNumberHandling)(-1); } }", true)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().NumberHandling = (JsonNumberHandling)8; } }", true)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().NumberHandling = (JsonNumberHandling)7; } }", false)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().NumberHandling = JsonNumberHandling.Strict; } }", false)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().PreferredObjectCreationHandling = (JsonObjectCreationHandling)3; } }", true)]
    [InlineData("using System.Text.Json; using System.Text.Json.Serialization; class C { public void M() { new JsonSerializerOptions().PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().NewLine = \"\"; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().NewLine = \"ab\"; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().NewLine = \"\\r\"; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().NewLine = \"\\n\"; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { new JsonSerializerOptions().NewLine = \"\\r\\n\"; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M(string v) { new JsonSerializerOptions().NewLine = v; } }", false)]
    public async Task InvalidJsonSerializerOptionsPropertyAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidJsonSerializerOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidJsonSerializerOptionsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Xml; class C { public void M() { new XmlWriterSettings().ConformanceLevel = (ConformanceLevel)99; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlWriterSettings().ConformanceLevel = ConformanceLevel.Document; } }", false)]
    [InlineData("using System.Xml; class C { public void M() { new XmlWriterSettings().NewLineHandling = (NewLineHandling)99; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlWriterSettings().NamespaceHandling = (NamespaceHandling)99; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlWriterSettings().NamespaceHandling = (NamespaceHandling)1; } }", false)]
    public async Task InvalidXmlWriterSettingsPropertyAnalyzer_Should_FlagOnlyOutOfRangeEnumValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlWriterSettingsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlWriterSettingsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Xml; class C { public void M() { new XmlReaderSettings().DtdProcessing = (DtdProcessing)99; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlReaderSettings().ValidationType = (ValidationType)99; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlReaderSettings().MaxCharactersInDocument = -1; } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlReaderSettings().MaxCharactersInDocument = 0; } }", false)]
    [InlineData("using System.Xml; class C { public void M() { new XmlReaderSettings().MaxCharactersFromEntities = -1; } }", true)]
    [InlineData("using System.Xml; class C { public void M(long v) { new XmlReaderSettings().MaxCharactersInDocument = v; } }", false)]
    [InlineData("class XmlReaderSettings { public long MaxCharactersInDocument { get; set; } } class C { public void M() { new XmlReaderSettings().MaxCharactersInDocument = -1; } }", false)]
    [InlineData("using System.Xml; using System.Xml.Schema; class C { public void M() { new XmlReaderSettings().ValidationFlags = (XmlSchemaValidationFlags)32; } }", true)]
    [InlineData("using System.Xml; using System.Xml.Schema; class C { public void M() { new XmlReaderSettings().ValidationFlags = (XmlSchemaValidationFlags)31; } }", false)]
    [InlineData("using System.Xml; using System.Xml.Schema; class C { public void M() { new XmlReaderSettings().ValidationFlags = (XmlSchemaValidationFlags)0; } }", false)]
    public async Task InvalidXmlReaderSettingsPropertyAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlReaderSettingsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlReaderSettingsPropertyAnalyzer.DiagnosticId));
    }

    // ── Three hundred eighty-second through eighty-fifth batches (451-й проход) — four new analyzers.

    [Theory]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new HttpClient().Timeout = TimeSpan.Zero; } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new HttpClient().Timeout = TimeSpan.FromSeconds(-5); } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new HttpClient().Timeout = TimeSpan.FromSeconds(30); } }", false)]
    [InlineData("using System; using System.Threading; using System.Net.Http; class C { public void M() { new HttpClient().Timeout = Timeout.InfiniteTimeSpan; } }", false)]
    [InlineData("using System; using System.Net.Http; class C { public void M(TimeSpan t) { new HttpClient().Timeout = t; } }", false)]
    public async Task InvalidHttpClientTimeoutAnalyzer_Should_FlagOnlyInvalidTimeouts(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHttpClientTimeoutAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHttpClientTimeoutAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Net.Http; class C { public void M() { new HttpClient().MaxResponseContentBufferSize = 0; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M() { new HttpClient().MaxResponseContentBufferSize = 1000; } }", false)]
    [InlineData("using System.Net.Http; class C { public void M() { new HttpClientHandler().MaxConnectionsPerServer = 0; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M() { new HttpClientHandler().MaxConnectionsPerServer = 5; } }", false)]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().MaxAutomaticRedirections = -1; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().MaxResponseHeadersLength = 0; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M(int v) { new HttpClientHandler().MaxConnectionsPerServer = v; } }", false)]
    [InlineData("class HttpClient { public int MaxConnectionsPerServer { get; set; } } class C { public void M() { new HttpClient().MaxConnectionsPerServer = 0; } }", false)]
    public async Task InvalidHttpNonPositiveLimitPropertyAnalyzer_Should_FlagOnlyNonPositiveValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHttpNonPositiveLimitPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHttpNonPositiveLimitPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().ConnectTimeout = TimeSpan.FromMilliseconds(-2); } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().ConnectTimeout = TimeSpan.Zero; } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().ConnectTimeout = TimeSpan.FromSeconds(5); } }", false)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().PooledConnectionLifetime = TimeSpan.Zero; } }", false)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().PooledConnectionLifetime = TimeSpan.FromMilliseconds(-2); } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().KeepAlivePingTimeout = TimeSpan.Zero; } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().KeepAlivePingTimeout = TimeSpan.FromSeconds(1); } }", false)]
    [InlineData("using System; using System.Net.Http; class C { public void M() { new SocketsHttpHandler().KeepAlivePingDelay = TimeSpan.FromMilliseconds(500); } }", true)]
    [InlineData("using System; using System.Net.Http; class C { public void M(TimeSpan t) { new SocketsHttpHandler().ConnectTimeout = t; } }", false)]
    public async Task InvalidSocketsHttpHandlerTimeoutAnalyzer_Should_FlagOnlyInvalidTimeouts(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSocketsHttpHandlerTimeoutAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSocketsHttpHandlerTimeoutAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().InitialHttp2StreamWindowSize = 0; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().InitialHttp2StreamWindowSize = 65535; } }", false)]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().InitialHttp2StreamWindowSize = 16777216; } }", false)]
    [InlineData("using System.Net.Http; class C { public void M() { new SocketsHttpHandler().InitialHttp2StreamWindowSize = 16777217; } }", true)]
    [InlineData("using System.Net.Http; class C { public void M(int v) { new SocketsHttpHandler().InitialHttp2StreamWindowSize = v; } }", false)]
    public async Task InvalidSocketsHttpHandlerStreamWindowSizeAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSocketsHttpHandlerStreamWindowSizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSocketsHttpHandlerStreamWindowSizeAnalyzer.DiagnosticId));
    }

    // ── Three hundred eighty-sixth through eighty-eighth batches (452-й проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Diagnostics; class C { public void M() { new ProcessStartInfo().WindowStyle = (ProcessWindowStyle)999; } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { new ProcessStartInfo().WindowStyle = (ProcessWindowStyle)(-1); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { new ProcessStartInfo().WindowStyle = ProcessWindowStyle.Hidden; } }", false)]
    [InlineData("using System.Diagnostics; class C { public void M(ProcessWindowStyle w) { new ProcessStartInfo().WindowStyle = w; } }", false)]
    [InlineData("class ProcessStartInfo { public int WindowStyle { get; set; } } class C { public void M() { new ProcessStartInfo().WindowStyle = 999; } }", false)]
    public async Task InvalidProcessStartInfoWindowStyleAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidProcessStartInfoWindowStyleAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidProcessStartInfoWindowStyleAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Net.Cache; class C { public void M() { var p = new HttpRequestCachePolicy((HttpRequestCacheLevel)999); } }", true)]
    [InlineData("using System.Net.Cache; class C { public void M() { var p = new HttpRequestCachePolicy(HttpRequestCacheLevel.Default); } }", false)]
    [InlineData("using System.Net.Cache; class C { public void M() { var p = new RequestCachePolicy((RequestCacheLevel)999); } }", true)]
    [InlineData("using System.Net.Cache; class C { public void M() { var p = new RequestCachePolicy(RequestCacheLevel.Default); } }", false)]
    [InlineData("using System.Net.Cache; class C { public void M(HttpRequestCacheLevel l) { var p = new HttpRequestCachePolicy(l); } }", false)]
    [InlineData("class HttpRequestCachePolicy { public HttpRequestCachePolicy(int l) {} } class C { public void M() { var p = new HttpRequestCachePolicy(999); } }", false)]
    public async Task InvalidCachePolicyConstructorAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCachePolicyConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCachePolicyConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.ContinueWith(_ => { }, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.OnlyOnRanToCompletion); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.ContinueWith(_ => { }, TaskContinuationOptions.LongRunning | TaskContinuationOptions.ExecuteSynchronously); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.ContinueWith(_ => { }, TaskContinuationOptions.LongRunning | TaskContinuationOptions.PreferFairness); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t) => t.ContinueWith(_ => { }, TaskContinuationOptions.None); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task t, TaskContinuationOptions o) => t.ContinueWith(_ => { }, o); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task<int> t) => t.ContinueWith(_ => { }, TaskContinuationOptions.LongRunning | TaskContinuationOptions.ExecuteSynchronously); }", true)]
    [InlineData("class Task { public void ContinueWith(System.Action<int> a, int o) {} } class C { public void M(Task t) => t.ContinueWith(x => { }, 999); }", false)]
    public async Task InvalidTaskContinuationOptionsCombinationAnalyzer_Should_FlagOnlyConflictingCombinations(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTaskContinuationOptionsCombinationAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTaskContinuationOptionsCombinationAnalyzer.DiagnosticId));
    }

    // ── Three hundred eighty-ninth through ninety-third batches (453-й проход) — four new analyzers
    // (plus the InvalidXmlReaderSettingsPropertyAnalyzer/RPAD0445 ValidationFlags extension above).

    [Theory]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonDocumentOptions(); o.CommentHandling = JsonCommentHandling.Allow; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonDocumentOptions(); o.CommentHandling = JsonCommentHandling.Skip; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonReaderOptions(); o.CommentHandling = JsonCommentHandling.Allow; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonReaderOptions(); o.CommentHandling = (JsonCommentHandling)99; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonReaderOptions(); o.MaxDepth = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.MaxDepth = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonDocumentOptions(); o.MaxDepth = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonReaderOptions(); o.MaxDepth = 0; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M(int v) { var o = new JsonReaderOptions(); o.MaxDepth = v; } }", false)]
    [InlineData("class JsonReaderOptions { public int MaxDepth { get; set; } } class C { public void M() { new JsonReaderOptions().MaxDepth = -1; } }", false)]
    public async Task InvalidJsonOptionsPropertyAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidJsonOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidJsonOptionsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: 100, resumeWriterThreshold: 101); } }", true)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: -5, resumeWriterThreshold: -1); } }", true)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: -1); } }", true)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: 100, resumeWriterThreshold: 100); } }", false)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: -1, resumeWriterThreshold: 0); } }", false)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(pauseWriterThreshold: 100); } }", true)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(resumeWriterThreshold: 50); } }", false)]
    [InlineData("using System.IO.Pipelines; class C { public void M(int r) { var p = new PipeOptions(pauseWriterThreshold: 100, resumeWriterThreshold: r); } }", false)]
    [InlineData("using System.IO.Pipelines; class C { public void M() { var p = new PipeOptions(minimumSegmentSize: 64); } }", false)]
    [InlineData("class PipeOptions { public PipeOptions(int pauseWriterThreshold, int resumeWriterThreshold) {} } class C { public void M() { var p = new PipeOptions(1, -1); } }", false)]
    public async Task InvalidPipeOptionsConstructorAnalyzer_Should_FlagOnlyInvalidCombinations(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPipeOptionsConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPipeOptionsConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public void M() { new ParallelOptions().MaxDegreeOfParallelism = 0; } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M() { new ParallelOptions().MaxDegreeOfParallelism = -2; } }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M() { new ParallelOptions().MaxDegreeOfParallelism = -1; } }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M() { new ParallelOptions().MaxDegreeOfParallelism = 4; } }", false)]
    [InlineData("using System.Threading.Tasks; class C { public void M(int v) { new ParallelOptions().MaxDegreeOfParallelism = v; } }", false)]
    public async Task InvalidParallelOptionsMaxDegreeOfParallelismAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidParallelOptionsMaxDegreeOfParallelismAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidParallelOptionsMaxDegreeOfParallelismAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Channels; class C { public void M() { var o = new BoundedChannelOptions(-1); } }", true)]
    [InlineData("using System.Threading.Channels; class C { public void M() { var o = new BoundedChannelOptions(0); } }", false)]
    [InlineData("using System.Threading.Channels; class C { public void M() { var o = new BoundedChannelOptions(5); } }", false)]
    [InlineData("using System.Threading.Channels; class C { public void M() { new BoundedChannelOptions(5).FullMode = (BoundedChannelFullMode)99; } }", true)]
    [InlineData("using System.Threading.Channels; class C { public void M() { new BoundedChannelOptions(5).FullMode = BoundedChannelFullMode.Wait; } }", false)]
    [InlineData("using System.Threading.Channels; class C { public void M(int c) { var o = new BoundedChannelOptions(c); } }", false)]
    public async Task InvalidBoundedChannelOptionsAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBoundedChannelOptionsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBoundedChannelOptionsAnalyzer.DiagnosticId));
    }

    // ── Three hundred ninety-fourth through ninety-seventh batches (454-й проход) — three new analyzers
    // (plus the InvalidJsonSerializerOptionsPropertyAnalyzer/RPAD0443 extension above).

    [Theory]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new ExecutionDataflowBlockOptions().BoundedCapacity = 0; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new ExecutionDataflowBlockOptions().BoundedCapacity = -2; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new ExecutionDataflowBlockOptions().BoundedCapacity = -1; } }", false)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new GroupingDataflowBlockOptions().BoundedCapacity = 0; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new ExecutionDataflowBlockOptions().MaxDegreeOfParallelism = 0; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new ExecutionDataflowBlockOptions().MaxDegreeOfParallelism = -1; } }", false)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M(int v) { new ExecutionDataflowBlockOptions().BoundedCapacity = v; } }", false)]
    public async Task InvalidDataflowBlockOptionsPropertyAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDataflowBlockOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDataflowBlockOptionsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { var b = new BatchBlock<int>(0); } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { var b = new BatchBlock<int>(-1); } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { var b = new BatchBlock<int>(1); } }", false)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { var b = new BatchedJoinBlock<int, int>(0); } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { var b = new BatchedJoinBlock<int, int, int>(0); } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M(int n) { var b = new BatchBlock<int>(n); } }", false)]
    public async Task InvalidBatchBlockConstructorAnalyzer_Should_FlagOnlyNonPositiveBatchSizes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBatchBlockConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBatchBlockConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO.Compression; class C { public void M() { new BrotliCompressionOptions().Quality = -1; } }", true)]
    [InlineData("using System.IO.Compression; class C { public void M() { new BrotliCompressionOptions().Quality = 12; } }", true)]
    [InlineData("using System.IO.Compression; class C { public void M() { new BrotliCompressionOptions().Quality = 0; } }", false)]
    [InlineData("using System.IO.Compression; class C { public void M() { new BrotliCompressionOptions().Quality = 11; } }", false)]
    [InlineData("using System.IO.Compression; class C { public void M() { new ZLibCompressionOptions().CompressionLevel = -2; } }", true)]
    [InlineData("using System.IO.Compression; class C { public void M() { new ZLibCompressionOptions().CompressionLevel = 10; } }", true)]
    [InlineData("using System.IO.Compression; class C { public void M() { new ZLibCompressionOptions().CompressionLevel = -1; } }", false)]
    [InlineData("using System.IO.Compression; class C { public void M() { new ZLibCompressionOptions().CompressionStrategy = (ZLibCompressionStrategy)99; } }", true)]
    [InlineData("using System.IO.Compression; class C { public void M() { new ZLibCompressionOptions().CompressionStrategy = ZLibCompressionStrategy.Default; } }", false)]
    [InlineData("using System.IO.Compression; class C { public void M(int v) { new BrotliCompressionOptions().Quality = v; } }", false)]
    [InlineData("class BrotliCompressionOptions { public int Quality { get; set; } } class C { public void M() { new BrotliCompressionOptions().Quality = -1; } }", false)]
    public async Task InvalidCompressionOptionsPropertyAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCompressionOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCompressionOptionsPropertyAnalyzer.DiagnosticId));
    }

    // ── Three hundred ninety-eighth through four hundredth batches (455-й проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Level = (EventLevel)256; } }", true)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Level = (EventLevel)(-1); } }", true)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Level = (EventLevel)255; } }", false)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Level = EventLevel.LogAlways; } }", false)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Opcode = (EventOpcode)256; } }", true)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M() { var o = new EventSourceOptions(); o.Opcode = (EventOpcode)100; } }", false)]
    [InlineData("using System.Diagnostics.Tracing; class C { public void M(EventLevel l) { var o = new EventSourceOptions(); o.Level = l; } }", false)]
    [InlineData("class EventSourceOptions { public int Level { get; set; } } class C { public void M() { new EventSourceOptions().Level = 256; } }", false)]
    public async Task InvalidEventSourceOptionsPropertyAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEventSourceOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEventSourceOptionsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Diagnostics; class C { public void M() { new TraceSwitch(\"t\", \"d\").Level = (TraceLevel)(-100); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { new TraceSwitch(\"t\", \"d\").Level = (TraceLevel)5; } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { new TraceSwitch(\"t\", \"d\").Level = (TraceLevel)0; } }", false)]
    [InlineData("using System.Diagnostics; class C { public void M() { new TraceSwitch(\"t\", \"d\").Level = (TraceLevel)4; } }", false)]
    [InlineData("using System.Diagnostics; class C { public void M(TraceLevel l) { new TraceSwitch(\"t\", \"d\").Level = l; } }", false)]
    [InlineData("class TraceSwitch { public int Level { get; set; } } class C { public void M() { new TraceSwitch().Level = 5; } }", false)]
    public async Task InvalidTraceSwitchLevelAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTraceSwitchLevelAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTraceSwitchLevelAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new DataflowLinkOptions().MaxMessages = 0; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new DataflowLinkOptions().MaxMessages = -2; } }", true)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new DataflowLinkOptions().MaxMessages = -1; } }", false)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M() { new DataflowLinkOptions().MaxMessages = 5; } }", false)]
    [InlineData("using System.Threading.Tasks.Dataflow; class C { public void M(int v) { new DataflowLinkOptions().MaxMessages = v; } }", false)]
    public async Task InvalidDataflowLinkOptionsMaxMessagesAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDataflowLinkOptionsMaxMessagesAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDataflowLinkOptionsMaxMessagesAnalyzer.DiagnosticId));
    }

    // ── Four hundred first through third batches (456-й проход) — three new analyzers.

    // The AEAD analyzers deliberately stay silent when the cipher's IsSupported is false on the current
    // platform (e.g. AesCcm on macOS), so `expectFlagged` is the verdict on platforms that support `cipher`;
    // `cipher: null` marks lookalike types that are never flagged.
    private static bool IsAeadCipherSupported(string? cipher) => cipher switch
    {
        null => true,
        nameof(AesGcm) => AesGcm.IsSupported,
        nameof(AesCcm) => AesCcm.IsSupported,
        nameof(ChaCha20Poly1305) => ChaCha20Poly1305.IsSupported,
        _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher, null),
    };

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesGcm(new byte[15], 16); } }", true, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesGcm(new byte[16], 16); } }", false, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesGcm(new byte[24], 16); } }", false, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesGcm(new byte[16], 11); } }", true, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesCcm(new byte[15]); } }", true, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new AesCcm(new byte[16]); } }", false, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new ChaCha20Poly1305(new byte[16]); } }", true, "ChaCha20Poly1305")]
    [InlineData("using System.Security.Cryptography; class C { public void M() { using var x = new ChaCha20Poly1305(new byte[32]); } }", false, "ChaCha20Poly1305")]
    [InlineData("using System.Security.Cryptography; class C { public void M(byte[] k) { using var x = new AesGcm(k, 16); } }", false, "AesGcm")]
    [InlineData("class AesGcm { public AesGcm(byte[] k) {} } class C { public void M() { var x = new AesGcm(new byte[15]); } }", false, null)]
    public async Task InvalidAeadCipherConstructorArgumentAnalyzer_Should_FlagOnlyInvalidKeyOrTagSizes(string code, bool expectFlagged, string? cipher)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAeadCipherConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged && IsAeadCipherSupported(cipher), diagnostics.Any(d => d.Id == InvalidAeadCipherConstructorArgumentAnalyzer.DiagnosticId));
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    // AesGcm's accepted tag sizes are platform-specific (12-16 on Windows/Linux, only 16 on macOS), so
    // the 12-16 range is checked against the platform's own public AesGcm.TagByteSizes metadata.
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public async Task InvalidAeadCipherConstructorArgumentAnalyzer_Should_FlagAesGcmTagSize_OnlyWhenThePlatformRejectsIt(int tagSize)
    {
        var code = $"using System.Security.Cryptography; class C {{ public void M() {{ using var x = new AesGcm(new byte[16], {tagSize}); }} }}";
        var sizes = AesGcm.TagByteSizes;
        var isLegalSize = tagSize >= sizes.MinSize && tagSize <= sizes.MaxSize
            && (sizes.SkipSize == 0 || (tagSize - sizes.MinSize) % sizes.SkipSize == 0);

        var diagnostics = await GetDiagnosticsAsync(new InvalidAeadCipherConstructorArgumentAnalyzer(), code);
        Assert.Equal(AesGcm.IsSupported && !isLegalSize, diagnostics.Any(d => d.Id == InvalidAeadCipherConstructorArgumentAnalyzer.DiagnosticId));
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(-5); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); } }", false)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(0); } }", false)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int n) { var b = ImmutableArray.CreateBuilder<int>(n); } }", false)]
    [InlineData("class ImmutableArray { public static object CreateBuilder<T>(int c) => null; } class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(-5); } }", false)]
    public async Task InvalidImmutableArrayCreateBuilderCapacityAnalyzer_Should_FlagOnlyNegativeCapacities(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImmutableArrayCreateBuilderCapacityAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImmutableArrayCreateBuilderCapacityAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public void M(int[] arr) { var x = ImmutableArray.Create(arr, -1, 2); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int[] arr) { var x = ImmutableArray.Create(arr, 1, -5); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int[] arr) { var x = ImmutableArray.Create(arr, 0, 3); } }", false)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int[] arr) { var x = ImmutableArray.Create(arr, 3, 0); } }", false)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int[] arr, int s, int l) { var x = ImmutableArray.Create(arr, s, l); } }", false)]
    [InlineData("class ImmutableArray { public static object Create(int[] a, int s, int l) => null; } class C { public void M(int[] arr) { var x = ImmutableArray.Create(arr, -1, 2); } }", false)]
    public async Task InvalidImmutableArrayCreateRangeAnalyzer_Should_FlagOnlyNegativeStartOrLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImmutableArrayCreateRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImmutableArrayCreateRangeAnalyzer.DiagnosticId));
    }

    // ── Four hundred fourth through ninth batches (457-й проход) — six new analyzers.

    [Theory]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new PaxTarEntry(TarEntryType.ContiguousFile, \"n\"); } }", true)]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new PaxTarEntry(TarEntryType.RegularFile, \"n\"); } }", false)]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new GnuTarEntry(TarEntryType.ContiguousFile, \"n\"); } }", true)]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new UstarTarEntry(TarEntryType.ContiguousFile, \"n\"); } }", true)]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new V7TarEntry(TarEntryType.RegularFile, \"n\"); } }", true)]
    [InlineData("using System.Formats.Tar; class C { public void M() { var e = new V7TarEntry(TarEntryType.V7RegularFile, \"n\"); } }", false)]
    [InlineData("using System.Formats.Tar; class C { public void M(TarEntryType t) { var e = new PaxTarEntry(t, \"n\"); } }", false)]
    public async Task InvalidTarEntryConstructorAnalyzer_Should_FlagOnlyUnsupportedEntryTypes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTarEntryConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTarEntryConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter((AsnEncodingRules)99); } }", true)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); } }", false)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var r = new AsnReader(new byte[]{0}, (AsnEncodingRules)99); } }", true)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var r = new AsnReader(new byte[]{0}, AsnEncodingRules.DER); } }", false)]
    [InlineData("using System.Formats.Asn1; class C { public void M(AsnEncodingRules e) { var w = new AsnWriter(e); } }", false)]
    public async Task InvalidAsnEncodingRulesConstructorAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAsnEncodingRulesConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidAsnEncodingRulesConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteCharacterString(UniversalTagNumber.Boolean, \"x\"); } }", true)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteCharacterString(UniversalTagNumber.UTF8String, \"x\"); } }", false)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteObjectIdentifier(\"\"); } }", true)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteObjectIdentifier(\"not.an.oid\"); } }", true)]
    [InlineData("using System.Formats.Asn1; class C { public void M() { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteObjectIdentifier(\"1.2.840.113549.1.1.1\"); } }", false)]
    [InlineData("using System.Formats.Asn1; class C { public void M(string oid) { var w = new AsnWriter(AsnEncodingRules.DER); w.WriteObjectIdentifier(oid); } }", false)]
    public async Task InvalidAsnWriterMethodArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAsnWriterMethodArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidAsnWriterMethodArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => Rfc2898DeriveBytes.Pbkdf2(new byte[16], new byte[16], 0, HashAlgorithmName.SHA256, 32); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => Rfc2898DeriveBytes.Pbkdf2(new byte[16], new byte[16], 1, HashAlgorithmName.SHA256, 32); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M() => Rfc2898DeriveBytes.Pbkdf2(new byte[16], new byte[16], 1000, HashAlgorithmName.SHA256, -1); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] prk) => HKDF.Expand(HashAlgorithmName.SHA256, prk, 0); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] prk) => HKDF.Expand(HashAlgorithmName.SHA256, prk, 32); }", false)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(byte[] ikm) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 0); }", true)]
    [InlineData("using System.Security.Cryptography; class C { public byte[] M(int n) => Rfc2898DeriveBytes.Pbkdf2(new byte[16], new byte[16], n, HashAlgorithmName.SHA256, 32); }", false)]
    public async Task InvalidKeyDerivationOutputLengthAnalyzer_Should_FlagOnlyInvalidValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidKeyDerivationOutputLengthAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidKeyDerivationOutputLengthAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); b.RemoveRange(-1, 2); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); b.RemoveRange(0, -1); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); b.RemoveRange(0, 2); } }", false)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); b.Insert(-1, 1); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M() { var b = ImmutableArray.CreateBuilder<int>(5); b.CopyTo(new int[5], -1); } }", true)]
    [InlineData("using System.Collections.Immutable; class C { public void M(int i) { var b = ImmutableArray.CreateBuilder<int>(5); b.RemoveRange(i, 2); } }", false)]
    public async Task InvalidImmutableArrayBuilderRangeArgumentAnalyzer_Should_FlagOnlyNegativeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImmutableArrayBuilderRangeArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImmutableArrayBuilderRangeArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Formats.Tar; using System.IO; class C { public void M() { using var ms = new MemoryStream(); using var w = new TarWriter(ms, TarEntryFormat.Unknown); } }", true)]
    [InlineData("using System.Formats.Tar; using System.IO; class C { public void M() { using var ms = new MemoryStream(); using var w = new TarWriter(ms, TarEntryFormat.Pax); } }", false)]
    [InlineData("using System.Formats.Tar; using System.IO; class C { public void M(TarEntryFormat f) { using var ms = new MemoryStream(); using var w = new TarWriter(ms, f); } }", false)]
    public async Task InvalidTarWriterFormatAnalyzer_Should_FlagOnlyUndefinedFormats(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidTarWriterFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidTarWriterFormatAnalyzer.DiagnosticId));
    }

    // ── Four hundred tenth batch (458-й проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().Add(\"not-an-oid\", \"v\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().Add(\"1\", \"v\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().Add(\"\", \"v\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().Add(\"2.5.4.3\", \"v\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCountryOrRegion(\"USA\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCountryOrRegion(\"U1\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCountryOrRegion(\"US\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCountryOrRegion(\"us\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCommonName(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddCommonName(\"x\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddEmailAddress(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddEmailAddress(\"not-an-email\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X500DistinguishedNameBuilder().AddDomainComponent(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string v) { new X500DistinguishedNameBuilder().Add(v, \"x\"); } }", false)]
    [InlineData("class X500DistinguishedNameBuilder { public void Add(string a, string b) {} } class C { public void M() { new X500DistinguishedNameBuilder().Add(\"\", \"v\"); } }", false)]
    public async Task InvalidX500DistinguishedNameBuilderMethodAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX500DistinguishedNameBuilderMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX500DistinguishedNameBuilderMethodAnalyzer.DiagnosticId));
    }

    // ── Four hundred eleventh/twelfth/thirteenth batches (459-й проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddDnsName(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddDnsName(\"example.com\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddEmailAddress(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddEmailAddress(\"notanemail\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddUserPrincipalName(\"\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new SubjectAlternativeNameBuilder().AddUserPrincipalName(\"!!! not upn\"); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string v) { new SubjectAlternativeNameBuilder().AddDnsName(v); } }", false)]
    [InlineData("class SubjectAlternativeNameBuilder { public void AddDnsName(string a) {} } class C { public void M() { new SubjectAlternativeNameBuilder().AddDnsName(\"\"); } }", false)]
    public async Task InvalidSubjectAlternativeNameBuilderMethodAnalyzer_Should_FlagOnlyEmptyArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSubjectAlternativeNameBuilderMethodAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSubjectAlternativeNameBuilderMethodAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedInteger(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedInteger(0); } }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedInteger(0x1FFFFFFF); } }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedInteger(0x20000000); } }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedSignedInteger(-268435456); } }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedSignedInteger(-268435457); } }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedSignedInteger(268435455); } }", false)]
    [InlineData("using System.Reflection.Metadata; class C { public void M() { new BlobBuilder().WriteCompressedSignedInteger(268435456); } }", true)]
    [InlineData("using System.Reflection.Metadata; class C { public void M(int v) { new BlobBuilder().WriteCompressedInteger(v); } }", false)]
    [InlineData("class BlobBuilder { public void WriteCompressedInteger(int v) {} } class C { public void M() { new BlobBuilder().WriteCompressedInteger(-1); } }", false)]
    public async Task InvalidBlobBuilderCompressedIntegerAnalyzer_Should_FlagOnlyOutOfRangeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBlobBuilderCompressedIntegerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBlobBuilderCompressedIntegerAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xmlns\", \"http://foo\"); } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xmlns\", \"\"); } }", true)]
    [InlineData("using System.Xml; class C { public void M(string u) { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xmlns\", u); } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"XMLNS\", \"http://foo\"); } }", false)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xml\", \"http://bogus\"); } }", true)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xml\", \"http://www.w3.org/XML/1998/namespace\"); } }", false)]
    [InlineData("using System.Xml; class C { public void M(string u) { new XmlNamespaceManager(new NameTable()).AddNamespace(\"xml\", u); } }", false)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"p\", \"http://foo\"); } }", false)]
    [InlineData("using System.Xml; class C { public void M() { new XmlNamespaceManager(new NameTable()).AddNamespace(\"\", \"\"); } }", false)]
    [InlineData("using System.Xml; class C { public void M(string p) { new XmlNamespaceManager(new NameTable()).AddNamespace(p, \"http://foo\"); } }", false)]
    [InlineData("class XmlNamespaceManager { public void AddNamespace(string a, string b) {} } class C { public void M() { new XmlNamespaceManager().AddNamespace(\"xmlns\", \"x\"); } }", false)]
    public async Task InvalidXmlNamespaceManagerReservedPrefixAnalyzer_Should_FlagOnlyReservedPrefixes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXmlNamespaceManagerReservedPrefixAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXmlNamespaceManagerReservedPrefixAnalyzer.DiagnosticId));
    }

    // ── Four hundred fourteenth/fifteenth/sixteenth/seventeenth batches (459-й проход continued) —
    // NewLine extension to RPAD0443 (Theory above) plus three new analyzers.

    [Theory]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentSize = -1; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentSize = 0; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentSize = 127; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentSize = 128; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentCharacter = ' '; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentCharacter = '\\t'; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.IndentCharacter = 'a'; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.NewLine = \"\"; } }", true)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.NewLine = \"\\n\"; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M() { var o = new JsonWriterOptions(); o.NewLine = \"\\r\\n\"; } }", false)]
    [InlineData("using System.Text.Json; class C { public void M(int v) { var o = new JsonWriterOptions(); o.IndentSize = v; } }", false)]
    [InlineData("struct JsonWriterOptions { public int IndentSize; } class C { public void M() { var o = new JsonWriterOptions(); o.IndentSize = -1; } }", false)]
    public async Task InvalidJsonWriterOptionsPropertyAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidJsonWriterOptionsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidJsonWriterOptionsPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System; using System.Security.Cryptography; using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 cert) { new CertificateRevocationListBuilder().Build(cert, -1, DateTimeOffset.UtcNow, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); } }", true)]
    [InlineData("using System; using System.Security.Cryptography; using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 cert) { new CertificateRevocationListBuilder().Build(cert, 0, DateTimeOffset.UtcNow, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); } }", false)]
    [InlineData("using System; using System.Security.Cryptography; using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 cert) { new CertificateRevocationListBuilder().Build(cert, 1, DateTimeOffset.UtcNow, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); } }", false)]
    [InlineData("using System; using System.Security.Cryptography; using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 cert, int n) { new CertificateRevocationListBuilder().Build(cert, n, DateTimeOffset.UtcNow, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); } }", false)]
    [InlineData("class CertificateRevocationListBuilder { public byte[] Build(object c, System.Numerics.BigInteger n, object a, object b, object d) => null; } class C { public void M() { new CertificateRevocationListBuilder().Build(null, -1, null, null, null); } }", false)]
    public async Task InvalidCertificateRevocationListCrlNumberAnalyzer_Should_FlagOnlyNegativeCrlNumbers(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCertificateRevocationListCrlNumberAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCertificateRevocationListCrlNumberAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(-1, 1); } }", true)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0, 1); } }", false)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0x10000, 0); } }", true)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0, -1); } }", true)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0, 0x10000); } }", false)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0, 0x10001); } }", true)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0xFFFF, 1); } }", false)]
    [InlineData("using System.Text.Unicode; class C { public void M() { var r = new UnicodeRange(0xFFFF, 2); } }", true)]
    [InlineData("using System.Text.Unicode; class C { public void M(int v) { var r = new UnicodeRange(v, 1); } }", false)]
    [InlineData("struct UnicodeRange { public UnicodeRange(int a, int b) {} } class C { public void M() { var r = new UnicodeRange(-1, 1); } }", false)]
    public async Task InvalidUnicodeRangeConstructorAnalyzer_Should_FlagOnlyOutOfRangeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidUnicodeRangeConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidUnicodeRangeConstructorAnalyzer.DiagnosticId));
    }

    // ── Four hundred eighteenth/nineteenth/twentieth/twenty-first/twenty-second batches
    // (460-й проход continued) — five new analyzers.

    [Theory]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(-1, 0, 0, 0); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, 0, 0, 0); } }", false)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, -1, 0, 0); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, 0, -1, 0); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, 0, 0, -1); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, 0, 0, 1); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(0, 0, 0, 16); } }", false)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var b = new MetadataBuilder(v, 0, 0, 0); } }", false)]
    [InlineData("class MetadataBuilder { public MetadataBuilder(int a, int b, int c, int d) {} } class C { public void M() { var b = new MetadataBuilder(-1, 0, 0, 0); } }", false)]
    public async Task InvalidMetadataBuilderConstructorAnalyzer_Should_FlagOnlyInvalidOffsets(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMetadataBuilderConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMetadataBuilderConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.SetCapacity(TableIndex.Module, -1); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.SetCapacity(TableIndex.Module, 0); } }", false)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.SetCapacity((TableIndex)56, 0); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.GetRowCount((TableIndex)56); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.GetRowCount(TableIndex.Module); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.SetCapacity(HeapIndex.Guid, -1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.SetCapacity((HeapIndex)99, 0); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.ReserveUserString(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new MetadataBuilder(); b.ReserveUserString(0); } }", false)]
    [InlineData("using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var b = new MetadataBuilder(); b.ReserveUserString(v); } }", false)]
    public async Task InvalidMetadataBuilderMethodArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMetadataBuilderMethodArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMetadataBuilderMethodArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new InstructionEncoder(new BlobBuilder()); e.LoadLocal(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new InstructionEncoder(new BlobBuilder()); e.LoadLocal(0); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new InstructionEncoder(new BlobBuilder()); e.LoadArgument(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new InstructionEncoder(new BlobBuilder()); e.StoreArgument(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var e = new InstructionEncoder(new BlobBuilder()); e.LoadLocal(v); } }", false)]
    public async Task InvalidInstructionEncoderMethodArgumentAnalyzer_Should_FlagOnlyNegativeIndices(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidInstructionEncoderMethodArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidInstructionEncoderMethodArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers; class C { public void M() { using var o = MemoryPool<byte>.Shared.Rent(-1); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { using var o = MemoryPool<byte>.Shared.Rent(-2); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { using var o = MemoryPool<byte>.Shared.Rent(0); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { using var o = MemoryPool<byte>.Shared.Rent(1); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(int v) { using var o = MemoryPool<byte>.Shared.Rent(v); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { using var o = MemoryPool<int>.Shared.Rent(-2); } }", true)]
    public async Task InvalidMemoryPoolRentArgumentAnalyzer_Should_FlagOnlyValuesLessThanNegativeOne(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMemoryPoolRentArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMemoryPoolRentArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509SubjectKeyIdentifierExtension(\"\", false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509SubjectKeyIdentifierExtension(\"a\", false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509SubjectKeyIdentifierExtension(\"ab\", false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509SubjectKeyIdentifierExtension(\"zz\", false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string v) { var e = new X509SubjectKeyIdentifierExtension(v, false); } }", false)]
    [InlineData("class X509SubjectKeyIdentifierExtension { public X509SubjectKeyIdentifierExtension(string a, bool b) {} } class C { public void M() { var e = new X509SubjectKeyIdentifierExtension(\"\", false); } }", false)]
    public async Task InvalidX509SubjectKeyIdentifierExtensionConstructorAnalyzer_Should_FlagOnlyTooShortValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509SubjectKeyIdentifierExtensionConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509SubjectKeyIdentifierExtensionConstructorAnalyzer.DiagnosticId));
    }

    // ── Four hundred twenty-third/fourth/fifth/sixth batches (461-й проход continued) — four new analyzers.

    [Theory]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspective(1, 1, 0, 10); } }", true)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspective(1, 1, 1, 10); } }", false)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspective(1, 1, 1, 0); } }", true)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspective(1, 1, 10, 10); } }", true)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspective(0, 1, 1, 10); } }", false)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspectiveFieldOfView(0, 1, 1, 10); } }", true)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 1, 10); } }", false)]
    [InlineData("using System.Numerics; class C { public void M() { var m = Matrix4x4.CreatePerspectiveOffCenter(-1, 1, -1, 1, 0, 10); } }", true)]
    [InlineData("using System.Numerics; class C { public void M(float v) { var m = Matrix4x4.CreatePerspective(1, 1, v, 10); } }", false)]
    public async Task InvalidMatrix4x4PerspectiveArgumentAnalyzer_Should_FlagOnlyInvalidDistances(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMatrix4x4PerspectiveArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMatrix4x4PerspectiveArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509BasicConstraintsExtension(true, true, -1, false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509BasicConstraintsExtension(true, true, 0, false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509BasicConstraintsExtension(true, false, -1, false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(bool b) { var e = new X509BasicConstraintsExtension(true, b, -1, false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(int v) { var e = new X509BasicConstraintsExtension(true, true, v, false); } }", false)]
    public async Task InvalidX509BasicConstraintsExtensionConstructorAnalyzer_Should_FlagOnlyNegativePathLength(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509BasicConstraintsExtensionConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509BasicConstraintsExtensionConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(fileAlignment: 513); } }", true)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(fileAlignment: 512); } }", false)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(fileAlignment: 65536); } }", false)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(fileAlignment: 131072, sectionAlignment: 131072); } }", true)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(sectionAlignment: 300, fileAlignment: 512); } }", true)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(sectionAlignment: 512, fileAlignment: 1024); } }", true)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(sectionAlignment: 1024, fileAlignment: 1024); } }", false)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M(int v) { var b = new PEHeaderBuilder(fileAlignment: v); } }", false)]
    [InlineData("using System.Reflection.PortableExecutable; class C { public void M() { var b = new PEHeaderBuilder(imageBase: 0); } }", false)]
    public async Task InvalidPEHeaderBuilderConstructorAnalyzer_Should_FlagOnlyInvalidAlignments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPEHeaderBuilderConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPEHeaderBuilderConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().GenericTypeParameter(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().GenericTypeParameter(0); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().GenericTypeParameter(65536); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().GenericMethodTypeParameter(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().FunctionPointer(genericParameterCount: -1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); var h = MetadataTokens.EntityHandle(0x02000001); ps.AddParameter().Type().GenericInstantiation(h, 0, false); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); var h = MetadataTokens.EntityHandle(0x02000001); ps.AddParameter().Type().GenericInstantiation(h, 1, false); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var b = new BlobBuilder(); new BlobEncoder(b).MethodSignature().Parameters(1, out _, out var ps); ps.AddParameter().Type().GenericTypeParameter(v); } }", false)]
    public async Task InvalidSignatureTypeEncoderArgumentAnalyzer_Should_FlagOnlyOutOfRangeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSignatureTypeEncoderArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSignatureTypeEncoderArgumentAnalyzer.DiagnosticId));
    }

    // ── Four hundred twenty-seventh batch (462-й проход continued) — one new analyzer.

    [Theory]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(0); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(codeSize: 1, maxStack: -1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(codeSize: 1, maxStack: 65536); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(codeSize: 1, maxStack: 65535); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(codeSize: 1, exceptionRegionCount: -1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(codeSize: 1, exceptionRegionCount: 0); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(v); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var bb = new BlobBuilder(); var ie = new InstructionEncoder(bb); var e = new MethodBodyStreamEncoder(new BlobBuilder()); e.AddMethodBody(ie, -1, default, default); } }", false)]
    [InlineData("class MethodBodyStreamEncoder { public void AddMethodBody(int a) {} } class C { public void M() { new MethodBodyStreamEncoder().AddMethodBody(-1); } }", false)]
    public async Task InvalidMethodBodyStreamEncoderAddMethodBodyAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMethodBodyStreamEncoderAddMethodBodyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMethodBodyStreamEncoderAddMethodBodyAnalyzer.DiagnosticId));
    }

    // ── Four hundred twenty-eighth/ninth/thirtieth batches (463-й проход continued) — three new analyzers.

    [Theory]
    [InlineData("using System.Diagnostics; class C { public void M() { var s = new StackTrace(-1); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { var s = new StackTrace(0); } }", false)]
    [InlineData("using System.Diagnostics; using System; class C { public void M() { var s = new StackTrace(new Exception(), -1); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M() { var s = new StackTrace(-1, true); } }", true)]
    [InlineData("using System.Diagnostics; class C { public void M(int v) { var s = new StackTrace(v); } }", false)]
    [InlineData("class StackTrace { public StackTrace(int a) {} } class C { public void M() { var s = new StackTrace(-1); } }", false)]
    public async Task InvalidStackTraceSkipFramesAnalyzer_Should_FlagOnlyNegativeSkipFrames(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStackTraceSkipFramesAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStackTraceSkipFramesAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64Url.GetMaxDecodedLength(-1); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64Url.GetMaxDecodedLength(0); } }", false)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64Url.GetEncodedLength(1610612733); } }", false)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64Url.GetEncodedLength(1610612734); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64Url.GetEncodedLength(-1); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M(int v) { var n = Base64Url.GetMaxDecodedLength(v); } }", false)]
    public async Task InvalidBase64UrlLengthArgumentAnalyzer_Should_FlagOnlyOutOfRangeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBase64UrlLengthArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBase64UrlLengthArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Runtime.CompilerServices; class C { public void M() { var p = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(object), -1); } }", true)]
    [InlineData("using System.Runtime.CompilerServices; class C { public void M() { var p = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(object), 0); } }", false)]
    [InlineData("using System.Runtime.CompilerServices; class C { public void M(int v) { var p = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(object), v); } }", false)]
    [InlineData("class RuntimeHelpers { public static System.IntPtr AllocateTypeAssociatedMemory(System.Type t, int s) => default; } class C { public void M() { var p = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(object), -1); } }", false)]
    public async Task InvalidRuntimeHelpersAllocateTypeAssociatedMemoryAnalyzer_Should_FlagOnlyNegativeSize(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRuntimeHelpersAllocateTypeAssociatedMemoryAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRuntimeHelpersAllocateTypeAssociatedMemoryAnalyzer.DiagnosticId));
    }

    // ── Four hundred thirty-first/second/third/fourth batches (464-й проход continued) — four new analyzers.

    [Theory]
    [InlineData("using System.Buffers; class C { public void M() { var f = new StandardFormat('X', 100); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var f = new StandardFormat('X', 99); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var f = new StandardFormat('X', 254); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var f = new StandardFormat('X', 255); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(byte v) { var f = new StandardFormat('X', v); } }", false)]
    [InlineData("struct StandardFormat { public StandardFormat(char s, byte p) {} } class C { public void M() { var f = new StandardFormat('X', 100); } }", false)]
    public async Task InvalidStandardFormatConstructorAnalyzer_Should_FlagOnlyOutOfRangePrecision(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStandardFormatConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStandardFormatConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64.GetMaxDecodedFromUtf8Length(-1); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64.GetMaxDecodedFromUtf8Length(0); } }", false)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64.GetMaxEncodedToUtf8Length(1610612733); } }", false)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64.GetMaxEncodedToUtf8Length(1610612734); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M() { var n = Base64.GetMaxEncodedToUtf8Length(-1); } }", true)]
    [InlineData("using System.Buffers.Text; class C { public void M(int v) { var n = Base64.GetMaxDecodedFromUtf8Length(v); } }", false)]
    public async Task InvalidBase64LengthArgumentAnalyzer_Should_FlagOnlyOutOfRangeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBase64LengthArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBase64LengthArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(0); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(1); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(); var s = w.GetSpan(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(); var m = w.GetMemory(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(); w.Advance(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var w = new ArrayBufferWriter<byte>(); w.Advance(0); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(int v) { var w = new ArrayBufferWriter<byte>(); w.Advance(v); } }", false)]
    public async Task InvalidArrayBufferWriterArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayBufferWriterArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayBufferWriterArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public void M() { var f = new EncoderReplacementFallback(\"\\uD800\"); } }", true)]
    [InlineData("using System.Text; class C { public void M() { var f = new EncoderReplacementFallback(\"\"); } }", false)]
    [InlineData("using System.Text; class C { public void M() { var f = new DecoderReplacementFallback(\"\\uDC00\"); } }", true)]
    [InlineData("using System.Text; class C { public void M() { var f = new DecoderReplacementFallback(\"abc\"); } }", false)]
    [InlineData("using System.Text; class C { public void M(string v) { var f = new EncoderReplacementFallback(v); } }", false)]
    [InlineData("class EncoderReplacementFallback { public EncoderReplacementFallback(string s) {} } class C { public void M() { var f = new EncoderReplacementFallback(\"\\uD800\"); } }", false)]
    public async Task InvalidReplacementFallbackConstructorAnalyzer_Should_FlagOnlyUnpairedSurrogates(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidReplacementFallbackConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidReplacementFallbackConstructorAnalyzer.DiagnosticId));
    }

    // ── Four hundred thirty-fifth/sixth/seventh batches (465-й проход continued) — three new analyzers.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().RevocationMode = (X509RevocationMode)(-1); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().RevocationMode = (X509RevocationMode)3; } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().RevocationMode = (X509RevocationMode)0; } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().TrustMode = (X509ChainTrustMode)2; } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().TrustMode = (X509ChainTrustMode)1; } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().VerificationFlags = (X509VerificationFlags)4096; } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { new X509ChainPolicy().VerificationFlags = (X509VerificationFlags)4095; } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509RevocationMode m) { new X509ChainPolicy().RevocationMode = m; } }", false)]
    [InlineData("enum X509RevocationMode { A } class X509ChainPolicy { public X509RevocationMode RevocationMode { get; set; } } class C { public void M() { new X509ChainPolicy().RevocationMode = (X509RevocationMode)(-1); } }", false)]
    public async Task InvalidX509ChainPolicyPropertyAnalyzer_Should_FlagOnlyUndefinedEnumValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509ChainPolicyPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509ChainPolicyPropertyAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers; class C { public void M(byte[] a) { var s = new ReadOnlySequence<byte>(a, -1, 1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(byte[] a) { var s = new ReadOnlySequence<byte>(a, 0, -1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(byte[] a) { var s = new ReadOnlySequence<byte>(a, 0, 1); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(ReadOnlySequence<byte> s) { var t = s.Slice(-1, 1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(ReadOnlySequence<byte> s) { var t = s.Slice(0, -1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(ReadOnlySequence<byte> s) { var t = s.Slice(0, 1); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(byte[] a, int v) { var s = new ReadOnlySequence<byte>(a, v, 1); } }", false)]
    public async Task InvalidReadOnlySequenceArgumentAnalyzer_Should_FlagOnlyNegativeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidReadOnlySequenceArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidReadOnlySequenceArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new VectorEncoder(new BlobBuilder()); e.Count(-1); } }", true)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M() { var e = new VectorEncoder(new BlobBuilder()); e.Count(0); } }", false)]
    [InlineData("using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; class C { public void M(int v) { var e = new VectorEncoder(new BlobBuilder()); e.Count(v); } }", false)]
    [InlineData("class VectorEncoder { public void Count(int c) {} } class C { public void M() { new VectorEncoder().Count(-1); } }", false)]
    public async Task InvalidVectorEncoderCountAnalyzer_Should_FlagOnlyNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidVectorEncoderCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidVectorEncoderCountAnalyzer.DiagnosticId));
    }

    // ── Four hundred thirty-eighth/ninth/fortieth batches (466-й проход continued) — three new analyzers.

    [Theory]
    [InlineData("using System.Buffers; class C { public void M(SequenceReader<byte> r) { r.Advance(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(SequenceReader<byte> r) { r.Advance(0); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(SequenceReader<byte> r) { r.Rewind(-1); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(SequenceReader<byte> r) { r.Rewind(0); } }", false)]
    [InlineData("using System.Buffers; class C { public void M(SequenceReader<byte> r, long v) { r.Advance(v); } }", false)]
    public async Task InvalidSequenceReaderArgumentAnalyzer_Should_FlagOnlyNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidSequenceReaderArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidSequenceReaderArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { c.Find((X509FindType)99, \"x\", false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { c.Find((X509FindType)(-1), \"x\", false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { c.Find(X509FindType.FindBySubjectName, \"x\", false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { c.Find((X509FindType)99, null, false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c, X509FindType t) { c.Find(t, \"x\", false); } }", false)]
    public async Task InvalidX509Certificate2CollectionFindAnalyzer_Should_FlagOnlyUndefinedFindType(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509Certificate2CollectionFindAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509Certificate2CollectionFindAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public void M(BinaryReader r) { var b = r.ReadBytes(-1); } }", true)]
    [InlineData("using System.IO; class C { public void M(BinaryReader r) { var b = r.ReadBytes(0); } }", false)]
    [InlineData("using System.IO; class C { public void M(BinaryReader r, int v) { var b = r.ReadBytes(v); } }", false)]
    [InlineData("class BinaryReader { public byte[] ReadBytes(int c) => null; } class C { public void M() { new BinaryReader().ReadBytes(-1); } }", false)]
    public async Task InvalidBinaryReaderReadBytesArgumentAnalyzer_Should_FlagOnlyNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBinaryReaderReadBytesArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBinaryReaderReadBytesArgumentAnalyzer.DiagnosticId));
    }

    // ── Four hundred forty-first/second/third/fourth batches (467-й проход continued) — four new analyzers.

    [Theory]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var m = BitVector32.CreateMask(int.MinValue); } }", true)]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var m = BitVector32.CreateMask(0); } }", false)]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var m = BitVector32.CreateMask(-1); } }", false)]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var s = BitVector32.CreateSection(0); } }", true)]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var s = BitVector32.CreateSection(-1); } }", true)]
    [InlineData("using System.Collections.Specialized; class C { public void M() { var s = BitVector32.CreateSection(1); } }", false)]
    [InlineData("using System.Collections.Specialized; class C { public void M(int v) { var m = BitVector32.CreateMask(v); } }", false)]
    public async Task InvalidBitVector32ArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBitVector32ArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBitVector32ArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Reflection; class C { public void M() { var p = new ParameterModifier(0); } }", true)]
    [InlineData("using System.Reflection; class C { public void M() { var p = new ParameterModifier(-1); } }", true)]
    [InlineData("using System.Reflection; class C { public void M() { var p = new ParameterModifier(1); } }", false)]
    [InlineData("using System.Reflection; class C { public void M(int v) { var p = new ParameterModifier(v); } }", false)]
    public async Task InvalidParameterModifierConstructorAnalyzer_Should_FlagOnlyNonPositiveCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidParameterModifierConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidParameterModifierConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; using System.Text; class C { public void M(Stream s) { var r = new StreamReader(s, Encoding.UTF8, true, -1); } }", false)]
    [InlineData("using System.IO; using System.Text; class C { public void M(Stream s) { var r = new StreamReader(s, Encoding.UTF8, true, 0); } }", true)]
    [InlineData("using System.IO; using System.Text; class C { public void M(Stream s) { var w = new StreamWriter(s, Encoding.UTF8, -1); } }", false)]
    [InlineData("using System.IO; using System.Text; class C { public void M(Stream s) { var w = new StreamWriter(s, Encoding.UTF8, 0); } }", true)]
    [InlineData("using System.IO; using System.Text; class C { public void M(string path) { var r = new StreamReader(path, Encoding.UTF8, true, -1); } }", true)]
    [InlineData("using System.IO; using System.Text; class C { public void M(string path) { var w = new StreamWriter(path, true, Encoding.UTF8, -1); } }", true)]
    [InlineData("using System.IO; using System.Text; class C { public void M(string path) { var r = new StreamReader(path, Encoding.UTF8, true, 1); } }", false)]
    [InlineData("using System.IO; using System.Text; class C { public void M(Stream s, int v) { var r = new StreamReader(s, Encoding.UTF8, true, v); } }", false)]
    public async Task InvalidStreamReaderWriterBufferSizeAnalyzer_Should_FlagOnlyInvalidBufferSizes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStreamReaderWriterBufferSizeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStreamReaderWriterBufferSizeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.IO; class C { public void M(Stream s) { var b = new BufferedStream(s, 0); } }", true)]
    [InlineData("using System.IO; class C { public void M(Stream s) { var b = new BufferedStream(s, -1); } }", true)]
    [InlineData("using System.IO; class C { public void M(Stream s) { var b = new BufferedStream(s, 1); } }", false)]
    [InlineData("using System.IO; class C { public void M(Stream s, int v) { var b = new BufferedStream(s, v); } }", false)]
    public async Task InvalidBufferedStreamConstructorAnalyzer_Should_FlagOnlyNonPositiveBufferSize(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidBufferedStreamConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidBufferedStreamConstructorAnalyzer.DiagnosticId));
    }

    // ── Four hundred forty-sixth/seventh batches (469-й проход continued) — two new analyzers.

    [Theory]
    [InlineData("using System.Runtime.InteropServices; class C { public void M() { var h = GCHandle.Alloc(new object(), (GCHandleType)4); } }", true)]
    [InlineData("using System.Runtime.InteropServices; class C { public void M() { var h = GCHandle.Alloc(new object(), (GCHandleType)(-1)); } }", true)]
    [InlineData("using System.Runtime.InteropServices; class C { public void M() { var h = GCHandle.Alloc(new object(), GCHandleType.Normal); h.Free(); } }", false)]
    [InlineData("using System.Runtime.InteropServices; class C { public void M(GCHandleType t) { var h = GCHandle.Alloc(new object(), t); } }", false)]
    public async Task InvalidGCHandleAllocTypeAnalyzer_Should_FlagOnlyUndefinedGCHandleType(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidGCHandleAllocTypeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidGCHandleAllocTypeAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509Extension(\"\", new byte[] { 1, 2 }, false); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var e = new X509Extension(\"1.2.3\", new byte[] { 1, 2 }, false); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string oid) { var e = new X509Extension(oid, new byte[] { 1, 2 }, false); } }", false)]
    public async Task InvalidX509ExtensionConstructorAnalyzer_Should_FlagOnlyEmptyOid(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509ExtensionConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509ExtensionConstructorAnalyzer.DiagnosticId));
    }

    // ── Four hundred forty-ninth/fiftieth batches (469-й проход continued) — two new analyzers.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store((StoreName)0); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store((StoreName)9); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store(StoreName.My); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store((StoreLocation)0); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store(StoreLocation.CurrentUser); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store((StoreName)0, StoreLocation.CurrentUser); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store(StoreName.My, (StoreLocation)0); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var s = new X509Store(StoreName.My, StoreLocation.CurrentUser, (OpenFlags)int.MaxValue); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(StoreName n) { var s = new X509Store(n); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string s) { var st = new X509Store(s, (StoreLocation)0); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(string s) { var st = new X509Store(s, StoreLocation.CurrentUser); } }", false)]
    public async Task InvalidX509StoreConstructorAnalyzer_Should_FlagOnlyUndefinedEnumValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509StoreConstructorAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509StoreConstructorAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var l = new Pkcs12LoaderLimits(); l.MacIterationLimit = -1; } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var l = new Pkcs12LoaderLimits(); l.MacIterationLimit = 0; } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var l = new Pkcs12LoaderLimits(); l.MaxCertificates = -1; } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M() { var l = new Pkcs12LoaderLimits(); l.MaxCertificates = int.MaxValue; } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(int v) { var l = new Pkcs12LoaderLimits(); l.MaxCertificates = v; } }", false)]
    [InlineData("class Pkcs12LoaderLimits { public int MaxCertificates { get; set; } } class C { public void M() { new Pkcs12LoaderLimits().MaxCertificates = -1; } }", false)]
    public async Task InvalidPkcs12LoaderLimitsPropertyAnalyzer_Should_FlagOnlyNegativeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPkcs12LoaderLimitsPropertyAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPkcs12LoaderLimitsPropertyAnalyzer.DiagnosticId));
    }

    // ── Four hundred fifty-first/second batches (470-й проход continued) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d) { var c = X509CertificateLoader.LoadPkcs12(d, null, (X509KeyStorageFlags)48); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d) { var c = X509CertificateLoader.LoadPkcs12(d, null, (X509KeyStorageFlags)64); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d) { var c = X509CertificateLoader.LoadPkcs12(d, null, (X509KeyStorageFlags)(-1)); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d) { var c = X509CertificateLoader.LoadPkcs12(d, null, (X509KeyStorageFlags)3); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d) { var c = X509CertificateLoader.LoadPkcs12Collection(d, null, (X509KeyStorageFlags)48); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(System.ReadOnlySpan<byte> d) { var c = X509CertificateLoader.LoadPkcs12(d, default(System.ReadOnlySpan<char>), (X509KeyStorageFlags)48); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(byte[] d, X509KeyStorageFlags f) { var c = X509CertificateLoader.LoadPkcs12(d, null, f); } }", false)]
    public async Task InvalidX509CertificateLoaderKeyStorageFlagsAnalyzer_Should_FlagOnlyInvalidFlags(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509CertificateLoaderKeyStorageFlagsAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509CertificateLoaderKeyStorageFlagsAnalyzer.DiagnosticId));
    }

    // ── Four hundred fifty-third/fourth/fifth batches (472-й проход continued) — three new analyzers.

    [Theory]
    [InlineData("using System.Runtime.InteropServices; using System.Collections.Generic; class C { public void M(List<int> l) { CollectionsMarshal.SetCount(l, -1); } }", true)]
    [InlineData("using System.Runtime.InteropServices; using System.Collections.Generic; class C { public void M(List<int> l) { CollectionsMarshal.SetCount(l, 0); } }", false)]
    [InlineData("using System.Runtime.InteropServices; using System.Collections.Generic; class C { public void M(List<int> l, int v) { CollectionsMarshal.SetCount(l, v); } }", false)]
    [InlineData("class CollectionsMarshal { public static void SetCount<T>(System.Collections.Generic.List<T> l, int c) {} } class C { public void M(System.Collections.Generic.List<int> l) { CollectionsMarshal.SetCount(l, -1); } }", false)]
    public async Task InvalidCollectionsMarshalSetCountArgumentAnalyzer_Should_FlagOnlyNegativeCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCollectionsMarshalSetCountArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCollectionsMarshalSetCountArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(5, 5); } }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(5, 3); } }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(0, 10); } }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(0, 10, 0); } }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(0, 10, 1); } }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(5L, 5L); } }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(0L, 10L); } }", false)]
    [InlineData("using System.Collections.Concurrent; class C { public void M() { var p = Partitioner.Create(0L, 10L, 0L); } }", true)]
    [InlineData("using System.Collections.Concurrent; class C { public void M(int a, int b) { var p = Partitioner.Create(a, b); } }", false)]
    public async Task InvalidPartitionerCreateArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPartitionerCreateArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPartitionerCreateArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Numerics; class C { public void M(int[] a) { var v = new Vector<int>(a, -1); } }", true)]
    [InlineData("using System.Numerics; class C { public void M(int[] a) { var v = new Vector<int>(a, 0); } }", false)]
    [InlineData("using System.Numerics; class C { public void M(Vector<int> v) { var x = v[-1]; } }", true)]
    [InlineData("using System.Numerics; class C { public void M(Vector<int> v) { var x = v[0]; } }", false)]
    [InlineData("using System.Numerics; class C { public void M(Vector<int> v) { var x = v[8]; } }", false)]
    [InlineData("using System.Numerics; class C { public void M(Vector<int> v, int i) { var x = v[i]; } }", false)]
    [InlineData("class Vec { public int this[int i] => 0; } class C { public void M(Vec v) { var x = v[-1]; } }", false)]
    public async Task InvalidVectorOfTArgumentAnalyzer_Should_FlagOnlyNegativeIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidVectorOfTArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidVectorOfTArgumentAnalyzer.DiagnosticId));
    }

    // ── Four hundred fifty-seventh batch (473-й проход continued) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Unknown); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Cert); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Pfx); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Pkcs7); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.SerializedStore); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Authenticode); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.SerializedCert); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export((X509ContentType)999); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c) { var b = c.Export(X509ContentType.Unknown, \"pw\"); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { var b = c.Export(X509ContentType.Unknown); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { var b = c.Export(X509ContentType.Pkcs7); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { var b = c.Export(X509ContentType.SerializedStore); } }", false)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2Collection c) { var b = c.Export(X509ContentType.Authenticode); } }", true)]
    [InlineData("using System.Security.Cryptography.X509Certificates; class C { public void M(X509Certificate2 c, X509ContentType t) { var b = c.Export(t); } }", false)]
    public async Task InvalidX509ContentTypeExportAnalyzer_Should_FlagOnlyUnsupportedContentTypes(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidX509ContentTypeExportAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidX509ContentTypeExportAnalyzer.DiagnosticId));
    }

    // ── Four hundred fifty-ninth batch (474-й проход continued) — one new analyzer.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"abc\", -1); } }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"abc\", 0); } }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"\", -1); } }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"\", 0); } }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"\", 5); } }", true)]
    [InlineData("using System.Security.Cryptography; class C { public void M() { var s = RandomNumberGenerator.GetString(\"abc\", 10); } }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(string c) { var s = RandomNumberGenerator.GetString(c, -1); } }", false)]
    [InlineData("using System.Security.Cryptography; class C { public void M(int v) { var s = RandomNumberGenerator.GetString(\"abc\", v); } }", false)]
    public async Task InvalidRandomNumberGeneratorGetStringArgumentAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRandomNumberGeneratorGetStringArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRandomNumberGeneratorGetStringArgumentAnalyzer.DiagnosticId));
    }

    // ── Four hundred sixtieth/first batches (475-й проход continued) — two new analyzers.

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"\", \"ns\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"1bad\", \"ns\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"valid\", \"ns\"); } }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"valid\", \"\"); } }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"\"); } }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M(string s) { var n = XName.Get(s, \"ns\"); } }", false)]
    public async Task InvalidXNameGetTwoArgAnalyzer_Should_FlagOnlyInvalidLocalName(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidXNameGetTwoArgAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidXNameGetTwoArgAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Element(\"\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Element(\"1bad\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Element(\"valid\"); } }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Elements(\"1bad\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Attribute(\"1bad\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { el.SetAttributeValue(\"1bad\", \"v\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el) { var x = el.Descendants(\"1bad\"); } }", true)]
    [InlineData("using System.Xml.Linq; class C { public void M() { var n = XName.Get(\"\"); } }", false)]
    [InlineData("using System.Xml.Linq; class C { public void M(XElement el, string s) { var x = el.Element(s); } }", false)]
    [InlineData("class C { public void M(string s) { var x = s.Substring(0); } }", false)]
    public async Task InvalidImplicitXNameConversionAnalyzer_Should_FlagOnlyInvalidArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImplicitXNameConversionAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImplicitXNameConversionAnalyzer.DiagnosticId));
    }

    // ── Four hundred sixty-second/third batches (477-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Globalization; class C { public void M() { var c = new GregorianCalendar(); c.TwoDigitYearMax = 1; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new GregorianCalendar(); c.TwoDigitYearMax = 2050; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new GregorianCalendar(); c.TwoDigitYearMax = 10000; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.TwoDigitYearMax = 1; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.TwoDigitYearMax = 9700; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.TwoDigitYearMax = 5000; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new UmAlQuraCalendar(); c.TwoDigitYearMax = 1; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new UmAlQuraCalendar(); c.TwoDigitYearMax = 1400; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { GregorianCalendar c = new(); c.TwoDigitYearMax = 1; } }", true)]
    [InlineData("using System.Globalization; class C { public void M(int v) { var c = new GregorianCalendar(); c.TwoDigitYearMax = v; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new GregorianCalendar(); var x = c.TwoDigitYearMax; } }", false)]
    public async Task InvalidCalendarTwoDigitYearMaxAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCalendarTwoDigitYearMaxAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCalendarTwoDigitYearMaxAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.HijriAdjustment = 3; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.HijriAdjustment = -3; } }", true)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.HijriAdjustment = 2; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.HijriAdjustment = -2; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new HijriCalendar(); c.HijriAdjustment = 0; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { var c = new GregorianCalendar(); c.TwoDigitYearMax = 1; } }", false)]
    [InlineData("using System.Globalization; class C { public void M() { HijriCalendar c = new(); c.HijriAdjustment = 5; } }", true)]
    [InlineData("using System.Globalization; class C { public void M(int v) { var c = new HijriCalendar(); c.HijriAdjustment = v; } }", false)]
    public async Task InvalidHijriCalendarAdjustmentAnalyzer_Should_FlagOnlyOutOfRangeValues(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHijriCalendarAdjustmentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHijriCalendarAdjustmentAnalyzer.DiagnosticId));
    }

    // ── Four hundred sixty-fourth/fifth batches (478-й проход) — two new analyzers.

    [Theory]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesGcm g, byte[] pt, byte[] ct, byte[] tag) { g.Encrypt(new byte[8], pt, ct, tag); } }", true, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesGcm g, byte[] pt, byte[] ct, byte[] tag) { g.Encrypt(new byte[12], pt, ct, tag); } }", false, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesGcm g, byte[] nonce, byte[] pt, byte[] ct) { g.Encrypt(nonce, pt, ct, new byte[20]); } }", false, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesCcm c, byte[] pt, byte[] ct, byte[] tag) { c.Encrypt(new byte[20], pt, ct, tag); } }", true, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesCcm c, byte[] pt, byte[] ct, byte[] tag) { c.Encrypt(new byte[12], pt, ct, tag); } }", false, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesCcm c, byte[] nonce, byte[] pt, byte[] ct) { c.Encrypt(nonce, pt, ct, new byte[5]); } }", true, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesCcm c, byte[] nonce, byte[] pt, byte[] ct) { c.Encrypt(nonce, pt, ct, new byte[3]); } }", true, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesCcm c, byte[] nonce, byte[] pt, byte[] ct) { c.Encrypt(nonce, pt, ct, new byte[12]); } }", false, "AesCcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(ChaCha20Poly1305 cc, byte[] pt, byte[] ct, byte[] tag) { cc.Encrypt(new byte[8], pt, ct, tag); } }", true, "ChaCha20Poly1305")]
    [InlineData("using System.Security.Cryptography; class C { public void M(ChaCha20Poly1305 cc, byte[] nonce, byte[] pt, byte[] ct) { cc.Encrypt(nonce, pt, ct, new byte[4]); } }", true, "ChaCha20Poly1305")]
    [InlineData("using System.Security.Cryptography; class C { public void M(ChaCha20Poly1305 cc, byte[] pt, byte[] ct) { cc.Encrypt(new byte[12], pt, ct, new byte[16]); } }", false, "ChaCha20Poly1305")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesGcm g, byte[] ct, byte[] tag, byte[] pt) { g.Decrypt(new byte[8], ct, tag, pt); } }", true, "AesGcm")]
    [InlineData("using System; using System.Security.Cryptography; class C { public unsafe void M(AesGcm g, Span<byte> pt, Span<byte> ct, Span<byte> tag) { g.Encrypt(stackalloc byte[8], pt, ct, tag); } }", true, "AesGcm")]
    [InlineData("using System.Security.Cryptography; class C { public void M(AesGcm g, byte[] nonce, byte[] pt, byte[] ct, byte[] tag) { g.Encrypt(nonce, pt, ct, tag); } }", false, "AesGcm")]
    [InlineData("class C { public void Encrypt(byte[] nonce) {} public void M() { Encrypt(new byte[1]); } }", false, null)]
    public async Task InvalidAeadCipherEncryptDecryptArgumentAnalyzer_Should_FlagOnlyInvalidLengths(string code, bool expectFlagged, string? cipher)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidAeadCipherEncryptDecryptArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged && IsAeadCipherSupported(cipher), diagnostics.Any(d => d.Id == InvalidAeadCipherEncryptDecryptArgumentAnalyzer.DiagnosticId));
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    [Theory]
    [InlineData("using System.Text; class C { public void M() { var f = CompositeFormat.Parse(\"Hello {0\"); } }", true)]
    [InlineData("using System.Text; class C { public void M() { var f = CompositeFormat.Parse(\"Hello {-1}\"); } }", true)]
    [InlineData("using System.Text; class C { public void M() { var f = CompositeFormat.Parse(\"Hello } World\"); } }", true)]
    [InlineData("using System.Text; class C { public void M() { var f = CompositeFormat.Parse(\"Hello {0}\"); } }", false)]
    [InlineData("using System.Text; class C { public void M() { var f = CompositeFormat.Parse(\"100%{{escaped}}\"); } }", false)]
    [InlineData("using System.Text; class C { public void M(string s) { var f = CompositeFormat.Parse(s); } }", false)]
    [InlineData("using System.Text; class C { public void M() { var x = int.Parse(\"1\"); } }", false)]
    public async Task InvalidCompositeFormatLiteralAnalyzer_Should_FlagOnlyInvalidFormatStrings(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCompositeFormatLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCompositeFormatLiteralAnalyzer.DiagnosticId));
    }

    // ── Four hundred sixty-sixth/seventh/eighth batches (479-й проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Net; class C { public void M() { var c = new Cookie(\"n\",\"v\"); c.Port = \"80\"; } }", true)]
    [InlineData("using System.Net; class C { public void M() { var c = new Cookie(\"n\",\"v\"); c.Port = \"\\\"80\\\"\"; } }", false)]
    [InlineData("using System.Net; class C { public void M() { var c = new Cookie(\"n\",\"v\"); c.Port = \"\\\"80,443\\\"\"; } }", false)]
    [InlineData("using System.Net; class C { public void M() { var c = new Cookie(\"n\",\"v\"); c.Port = \"\\\"abc\\\"\"; } }", true)]
    [InlineData("using System.Net; class C { public void M() { var c = new Cookie(\"n\",\"v\",\"/p\",\"example.com\"); c.Port = \"80\"; } }", true)]
    [InlineData("using System.Net; class C { public void M(string p) { var c = new Cookie(\"n\",\"v\"); c.Port = p; } }", false)]
    [InlineData("using System.Net; class C { public void M() { var c = new C2(); c.Port = \"80\"; } } class C2 { public string Port { get; set; } = \"\"; }", false)]
    public async Task InvalidCookiePortLiteralAnalyzer_Should_FlagOnlyInvalidPortLists(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCookiePortLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCookiePortLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(-1); } }", true)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(0); } }", false)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(0, -1); } }", true)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(-1, -1); } }", true)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(0, 0); } }", false)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s) { var x = s.SubstringByTextElements(999); } }", false)]
    [InlineData("using System.Globalization; class C { public void M(StringInfo s, int v) { var x = s.SubstringByTextElements(v); } }", false)]
    [InlineData("class C { public void M(string s) { var x = s.Substring(0); } }", false)]
    public async Task InvalidStringInfoSubstringByTextElementsArgumentAnalyzer_Should_FlagOnlyNegativeArguments(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStringInfoSubstringByTextElementsArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStringInfoSubstringByTextElementsArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"G17\"); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"\"); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"G123456\"); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"ABC\"); } }", true)]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"X99\"); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var f = StandardFormat.Parse(\"X100\"); } }", true)]
    [InlineData("using System.Buffers; class C { public void M(string s) { var f = StandardFormat.Parse(s); } }", false)]
    [InlineData("using System.Buffers; class C { public void M() { var x = int.Parse(\"1\"); } }", false)]
    public async Task InvalidStandardFormatParseLiteralAnalyzer_Should_FlagOnlyInvalidFormatStrings(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidStandardFormatParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidStandardFormatParseLiteralAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task[] t) => Task.WaitAll(t, -2); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task[] t) => Task.WaitAll(t, -1); }", false)]
    [InlineData("using System.Threading.Tasks; class C { public int M(Task[] t) => Task.WaitAny(t, -2); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public int M(Task[] t) => Task.WaitAny(t, 0); }", false)]
    [InlineData("using System.Threading; class C { public bool M(WaitHandle[] h) => WaitHandle.WaitAll(h, -2); }", true)]
    [InlineData("using System.Threading; class C { public bool M(WaitHandle[] h) => WaitHandle.WaitAll(h, -1); }", false)]
    [InlineData("using System.Threading; class C { public int M(WaitHandle[] h) => WaitHandle.WaitAny(h, -2); }", true)]
    [InlineData("using System.Threading.Tasks; class C { public void M(Task[] t, int n) => Task.WaitAll(t, n); }", false)]
    public async Task InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer_Should_NotFlag_AValidWaitAllOrWaitAnyTimeout(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Threading; class C { public void M(CountdownEvent ce) => ce.Signal(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(CountdownEvent ce) => ce.Signal(0); }", true)]
    [InlineData("using System.Threading; class C { public void M(CountdownEvent ce) => ce.Signal(1); }", false)]
    [InlineData("using System.Threading; class C { public void M(CountdownEvent ce) => ce.AddCount(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(Barrier b) => b.AddParticipants(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(Barrier b) => b.AddParticipants(1); }", false)]
    [InlineData("using System.Threading; class C { public void M(Barrier b) => b.RemoveParticipants(-1); }", true)]
    [InlineData("using System.Threading; class C { public void M(CountdownEvent ce, int n) => ce.Signal(n); }", false)]
    public async Task InvalidCountdownEventOrBarrierParticipantCountAnalyzer_Should_NotFlag_APositiveCount(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCountdownEventOrBarrierParticipantCountAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCountdownEventOrBarrierParticipantCountAnalyzer.DiagnosticId));
    }

    // ── Two hundred and sixty-fourth through two hundred and sixty-seventh batches (триста шестьдесят второй проход) — two new analyzers.

    [Theory]
    [InlineData("using System.IO; using System.IO.Compression; class C { public GZipStream M(Stream s) => new GZipStream(s, (CompressionLevel)99); }", true)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public GZipStream M(Stream s) => new GZipStream(s, (CompressionLevel)(-1)); }", true)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public GZipStream M(Stream s) => new GZipStream(s, CompressionLevel.Optimal); }", false)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public DeflateStream M(Stream s) => new DeflateStream(s, (CompressionLevel)99); }", true)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public ZLibStream M(Stream s) => new ZLibStream(s, (CompressionLevel)99); }", true)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public BrotliStream M(Stream s) => new BrotliStream(s, (CompressionLevel)99); }", true)]
    [InlineData("using System.IO; using System.IO.Compression; class C { public GZipStream M(Stream s, CompressionLevel l) => new GZipStream(s, l); }", false)]
    public async Task InvalidCompressionStreamConstructorArgumentAnalyzer_Should_NotFlag_AValidCompressionLevel(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidCompressionStreamConstructorArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidCompressionStreamConstructorArgumentAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(-1); }", true)]
    [InlineData("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(99999); }", true)]
    [InlineData("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(65001); }", false)]
    [InlineData("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(\"invalid-name\"); }", true)]
    [InlineData("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(\"utf-8\"); }", false)]
    [InlineData("using System.Text; class C { public Encoding M(int cp) => Encoding.GetEncoding(cp); }", false)]
    [InlineData("using System.Text; class C { public Encoding M(string n) => Encoding.GetEncoding(n); }", false)]
    public async Task InvalidEncodingGetEncodingArgumentAnalyzer_Should_NotFlag_AValidEncoding(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidEncodingGetEncodingArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidEncodingGetEncodingArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and sixty-eighth batch (триста шестьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public int M() => int.Parse(\"abc\"); }", true)]
    [InlineData("class C { public int M() => int.Parse(\"123\"); }", false)]
    [InlineData("class C { public long M() => long.Parse(\"xyz\"); }", true)]
    [InlineData("class C { public byte M() => byte.Parse(\"x!\"); }", true)]
    [InlineData("class C { public double M() => double.Parse(\"abc\"); }", true)]
    [InlineData("class C { public double M() => double.Parse(\"1e5\"); }", false)]
    [InlineData("class C { public decimal M() => decimal.Parse(\"abc\"); }", true)]
    [InlineData("using System; class C { public Half M() => Half.Parse(\"abc\"); }", true)]
    [InlineData("using System; class C { public Int128 M() => Int128.Parse(\"abc\"); }", true)]
    [InlineData("using System; class C { public UInt128 M() => UInt128.Parse(\"abc\"); }", true)]
    [InlineData("class C { public int M(string s) => int.Parse(s); }", false)]
    [InlineData("class C { public int M() => int.Parse(\"300\"); }", false)]
    // Extended проход 438 (TODO #74, candidate #4 from the sixth research subagent survey) — Convert.ToXxx.
    [InlineData("class C { public int M() => System.Convert.ToInt32(\"abc\"); }", true)]
    [InlineData("class C { public double M() => System.Convert.ToDouble(\"abc\"); }", true)]
    [InlineData("class C { public decimal M() => System.Convert.ToDecimal(\"abc\"); }", true)]
    [InlineData("class C { public byte M() => System.Convert.ToByte(\"abc\"); }", true)]
    [InlineData("class C { public int M() => System.Convert.ToInt32(\"123\"); }", false)]
    [InlineData("class C { public char M() => System.Convert.ToChar(\"abc\"); }", false)]
    [InlineData("class C { public bool M() => System.Convert.ToBoolean(\"abc\"); }", false)]
    [InlineData("class C { public int M(string x) => System.Convert.ToInt32(x); }", false)]
    [InlineData("class Convert { public static int ToInt32(string s) => 0; } class C { public void M() => Convert.ToInt32(\"abc\"); }", false)]
    public async Task InvalidNumericParseFormatAnalyzer_Should_NotFlag_ANumericLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidNumericParseFormatAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidNumericParseFormatAnalyzer.DiagnosticId));
    }

    // ── Two hundred and seventieth through two hundred and seventy-fourth batches (триста шестьдесят пятый проход) — three new analyzers.

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableArray.Create(1, 2, 3)[10]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableArray.Create(1, 2, 3)[1]; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableArray<int>.Empty[0]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray.Create(1, 2, 3).RemoveAt(10); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray.Create(1, 2, 3).RemoveAt(1); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray.Create(1, 2, 3).Insert(10, 5); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray.Create(1, 2, 3).Insert(3, 5); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M(ImmutableArray<int> a) => a[10]; }", false)]
    public async Task InvalidImmutableArrayLiteralIndexerAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImmutableArrayLiteralIndexerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImmutableArrayLiteralIndexerAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableList.Create(1, 2, 3)[10]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableList.Create(1, 2, 3)[1]; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableList<int>.Empty[0]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableList<int> M() => ImmutableList.Create(1, 2, 3).RemoveAt(10); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableList<int> M() => ImmutableList.Create(1, 2, 3).Insert(10, 5); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableList<int> M() => ImmutableList.Create(1, 2, 3).Insert(3, 5); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M(ImmutableList<int> l) => l[10]; }", false)]
    public async Task InvalidImmutableListLiteralIndexerAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidImmutableListLiteralIndexerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidImmutableListLiteralIndexerAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public int M() => System.Array.BinarySearch(new int[] { 1, 2, 3 }, 0, 10, 1); }", true)]
    [InlineData("class C { public int M() => System.Array.BinarySearch(new int[] { 1, 2, 3 }, 2, 5, 1); }", true)]
    [InlineData("class C { public int M() => System.Array.BinarySearch(new int[] { 1, 2, 3 }, 0, 3, 1); }", false)]
    [InlineData("class C { public int M() => System.Array.BinarySearch(new int[] { 1, 2, 3 }, -1, 2, 1); }", false)]
    [InlineData("class C { public int M(int[] a) => System.Array.BinarySearch(a, 0, 10, 1); }", false)]
    public async Task InvalidArrayBinarySearchRangeArgumentAnalyzer_Should_NotFlag_AnInBoundsRange(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidArrayBinarySearchRangeArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidArrayBinarySearchRangeArgumentAnalyzer.DiagnosticId));
    }

    // ── Two hundred and seventy-sixth batch (триста шестьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableStack<int>.Empty.Peek(); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableStack<int> M() => ImmutableStack<int>.Empty.Pop(); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableQueue<int>.Empty.Peek(); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableQueue<int> M() => ImmutableQueue<int>.Empty.Dequeue(); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableStack.Create(1).Peek(); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M(ImmutableStack<int> s) => s.Peek(); }", false)]
    [InlineData("using System.Collections.Immutable; class C { public bool M() => ImmutableStack<int>.Empty.IsEmpty; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableStack<int> M() => ImmutableStack<int>.Empty.Pop(out var v); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableQueue<int> M() => ImmutableQueue<int>.Empty.Dequeue(out var v); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableStack<int> M(ImmutableStack<int> s) => s.Pop(out var v); }", false)]
    public async Task EmptyImmutableStackOrQueuePeekOrPopAnalyzer_Should_NotFlag_ANonemptyReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EmptyImmutableStackOrQueuePeekOrPopAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EmptyImmutableStackOrQueuePeekOrPopAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableDictionary<string, int>.Empty[\"k\"]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableSortedDictionary<string, int>.Empty[\"k\"]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M(ImmutableDictionary<string, int> d) => d[\"k\"]; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M(ImmutableSortedDictionary<string, int> d) => d[\"k\"]; }", false)]
    public async Task EmptyImmutableDictionaryIndexerAnalyzer_Should_NotFlag_ANonEmptyReceiver(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new EmptyImmutableDictionaryIndexerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == EmptyImmutableDictionaryIndexerAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("using System.Collections.Immutable; class C { public int M() => default(ImmutableArray<int>)[0]; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => default(ImmutableArray<int>).Length; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public bool M() => default(ImmutableArray<int>).IsEmpty; }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => default(ImmutableArray<int>).RemoveAt(0); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => default(ImmutableArray<int>).Insert(0, 1); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => default(ImmutableArray<int>).Add(1); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public bool M() => default(ImmutableArray<int>).Contains(1); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => default(ImmutableArray<int>).IndexOf(1); }", true)]
    [InlineData("using System.Collections.Immutable; class C { public bool M() => default(ImmutableArray<int>).IsDefault; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public bool M() => default(ImmutableArray<int>).IsDefaultOrEmpty; }", false)]
    [InlineData("using System.Collections.Immutable; class C { public int M() => ImmutableArray<int>.Empty[0]; }", false)]
    public async Task DefaultImmutableArrayMemberAccessAnalyzer_Should_NotFlag_ASafeMember(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new DefaultImmutableArrayMemberAccessAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == DefaultImmutableArrayMemberAccessAnalyzer.DiagnosticId));
    }

    [Theory]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[2..10]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[1..0]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[0..3]; }", false)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[5..]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[..10]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[..3]; }", false)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[2..]; }", false)]
    [InlineData("class C { public string M() => \"abc\"[2..10]; }", true)]
    [InlineData("class C { public string M() => \"abc\"[0..3]; }", false)]
    [InlineData("class C { public int[] M(int[] a) => a[2..10]; }", false)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[^5..]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[^2..]; }", false)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[..^5]; }", true)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[..^1]; }", false)]
    [InlineData("class C { public int[] M() => new[] { 1, 2, 3 }[^3..^1]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }[2..10]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }[1..0]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }[-1..2]; }", true)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M() => new List<int> { 1, 2, 3 }[0..3]; }", false)]
    [InlineData("using System.Collections.Generic; class C { public List<int> M(List<int> l) => l[2..10]; }", false)]
    public async Task RangeSliceOutOfRangeAnalyzer_Should_NotFlag_AnInBoundsSlice(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RangeSliceOutOfRangeAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RangeSliceOutOfRangeAnalyzer.DiagnosticId));
    }

    // ── Two hundred and eighty-seventh batch (триста шестьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.GetProperty(\"b\"); }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.GetProperty(\"a\"); }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement.GetProperty(\"a\"); }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"42\").RootElement.GetProperty(\"a\"); }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement[10]; }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement[-1]; }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement[1]; }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement[0]; }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":1}}\").RootElement.GetProperty(\"a\").GetProperty(\"b\"); }", true)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":1}}\").RootElement.GetProperty(\"a\").GetProperty(\"c\"); }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[{\\\"a\\\":1}]\").RootElement[0].GetProperty(\"a\"); }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[{\\\"a\\\":1}]\").RootElement[0].GetProperty(\"b\"); }", true)]
    [InlineData("using System.Text.Json; class C { public bool M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.TryGetProperty(\"b\", out _); }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M(string json) => JsonDocument.Parse(json).RootElement.GetProperty(\"a\"); }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M(JsonElement e) => e.GetProperty(\"a\"); }", false)]
    [InlineData("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":\\\"hi\\\"}\").RootElement.GetProperty(\"a\").GetInt32(); }", true)]
    [InlineData("using System.Text.Json; class C { public string? M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.GetProperty(\"a\").GetString(); }", true)]
    [InlineData("using System.Text.Json; class C { public bool M() => JsonDocument.Parse(\"{\\\"a\\\":true}\").RootElement.GetProperty(\"a\").GetBoolean(); }", false)]
    [InlineData("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":42}\").RootElement.GetProperty(\"a\").GetInt32(); }", false)]
    [InlineData("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":99999999999999}\").RootElement.GetProperty(\"a\").GetInt32(); }", true)]
    [InlineData("using System.Text.Json; class C { public System.Guid M() => JsonDocument.Parse(\"{\\\"a\\\":\\\"not-a-guid\\\"}\").RootElement.GetProperty(\"a\").GetGuid(); }", true)]
    [InlineData("using System.Text.Json; class C { public string? M() => JsonDocument.Parse(\"{\\\"a\\\":null}\").RootElement.GetProperty(\"a\").GetString(); }", false)]
    [InlineData("using System.Text.Json; class C { public byte[] M() => JsonDocument.Parse(\"{\\\"a\\\":\\\"not base64!!\\\"}\").RootElement.GetProperty(\"a\").GetBytesFromBase64(); }", true)]
    [InlineData("using System.Text.Json; class C { public int M(JsonElement e) => e.GetInt32(); }", false)]
    [InlineData("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":42}}\").RootElement.GetProperty(\"a\").GetProperty(\"c\").GetInt32(); }", false)]
    [InlineData("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":\\\"x\\\"}}\").RootElement.GetProperty(\"a\").GetProperty(\"c\").GetInt32(); }", true)]
    public async Task InvalidJsonElementPropertyOrIndexAnalyzer_Should_NotFlag_AValidAccess(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidJsonElementPropertyOrIndexAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidJsonElementPropertyOrIndexAnalyzer.DiagnosticId));
    }

    // ── Two hundred and ninety-fifth batch (триста семьдесят второй проход) — one new analyzer.

    [Theory]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"hello\"); }", true)]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"2024-01-01\"); }", false)]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"January 1, 2024\"); }", false)]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"January\"); }", true)]
    [InlineData("class C { public System.DateTime M() => System.DateTime.Parse(\"\"); }", true)]
    [InlineData("class C { public System.DateTime M(string s) => System.DateTime.Parse(s); }", false)]
    [InlineData("class C { public System.DateTimeOffset M() => System.DateTimeOffset.Parse(\"hello\"); }", true)]
    [InlineData("class C { public System.TimeSpan M() => System.TimeSpan.Parse(\"hello\"); }", true)]
    [InlineData("class C { public System.TimeSpan M() => System.TimeSpan.Parse(\"1:30:00\"); }", false)]
    [InlineData("class C { public System.DateOnly M() => System.DateOnly.Parse(\"hello\"); }", true)]
    [InlineData("class C { public System.TimeOnly M() => System.TimeOnly.Parse(\"hello\"); }", true)]
    [InlineData("class C { public System.Guid M() => System.Guid.Parse(\"hello\"); }", false)]
    [InlineData("class C { public int M() => int.Parse(\"hello\"); }", false)]
    public async Task InvalidDateOrTimeParseLiteralAnalyzer_Should_NotFlag_APossiblyValidLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidDateOrTimeParseLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidDateOrTimeParseLiteralAnalyzer.DiagnosticId));
    }

    // ── Two hundred and ninety-seventh batch (триста семьдесят третий проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"hello\"); }", true)]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"192.168.1.0/24\"); }", false)]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"192.168.1.0\"); }", true)]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"192.168.1.0/99\"); }", true)]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"::1/128\"); }", false)]
    [InlineData("using System.Net; class C { public IPNetwork M(string s) => IPNetwork.Parse(s); }", false)]
    [InlineData("using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"hello\"); }", false)]
    public async Task InvalidIPNetworkLiteralAnalyzer_Should_NotFlag_AValidCidrLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIPNetworkLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIPNetworkLiteralAnalyzer.DiagnosticId));
    }

    // ── Two hundred and ninety-eighth batch (триста семьдесят четвёртый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"hello\"); }", true)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"192.168.1.1:80\"); }", false)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"192.168.1.1\"); }", false)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"192.168.1.1:99999\"); }", true)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"[::1]:80\"); }", false)]
    [InlineData("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"\"); }", true)]
    [InlineData("using System.Net; class C { public IPEndPoint M(string s) => IPEndPoint.Parse(s); }", false)]
    [InlineData("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"hello\"); }", false)]
    public async Task InvalidIPEndPointLiteralAnalyzer_Should_NotFlag_AValidEndpointLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidIPEndPointLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidIPEndPointLiteralAnalyzer.DiagnosticId));
    }

    // ── Two hundred and ninety-ninth batch (триста семьдесят пятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"hello\"); }", true)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"00-14-22-01-23-45\"); }", false)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"00-14-22\"); }", false)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"\"); }", false)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"0\"); }", true)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"--001422\"); }", true)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"00-14:22\"); }", true)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"aabbcc\"); }", false)]
    [InlineData("using System.Net.NetworkInformation; class C { public PhysicalAddress M(string s) => PhysicalAddress.Parse(s); }", false)]
    public async Task InvalidPhysicalAddressLiteralAnalyzer_Should_NotFlag_AValidMacLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidPhysicalAddressLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidPhysicalAddressLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundredth batch (триста семьдесят шестой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"[1,2,3]\")[10]; }", true)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"[1,2,3]\")[-1]; }", true)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"[1,2,3]\")[1]; }", false)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonArray.Parse(\"[1,2,3]\")[10]; }", true)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"{\\\"a\\\":1}\")[0]; }", false)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M(string s) => JsonNode.Parse(s)[10]; }", false)]
    [InlineData("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"{\\\"a\\\":1}\")[\"missing\"]; }", false)]
    [InlineData("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement[10]; }", false)]
    public async Task InvalidJsonArrayNodeIndexerAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidJsonArrayNodeIndexerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidJsonArrayNodeIndexerAnalyzer.DiagnosticId));
    }

    // ── Three hundred and first batch (триста семьдесят седьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Numerics; class C { public float M() => new Vector2(1, 2)[2]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => new Vector2(1, 2)[1]; }", false)]
    [InlineData("using System.Numerics; class C { public float M() => new Vector2(1, 2)[-1]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => new Vector3(1, 2, 3)[3]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => new Vector4(1, 2, 3, 4)[4]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => new Quaternion(1, 2, 3, 4)[4]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => Matrix3x2.Identity[6, 0]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => Matrix3x2.Identity[0, 3]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => Matrix3x2.Identity[0, 0]; }", false)]
    [InlineData("using System.Numerics; class C { public float M() => Matrix4x4.Identity[5, 0]; }", true)]
    [InlineData("using System.Numerics; class C { public float M() => Matrix4x4.Identity[0, 0]; }", false)]
    [InlineData("using System.Numerics; class C { public float M(Vector2 v) => v[2]; }", true)]
    [InlineData("using System.Numerics; class C { public float M(int i) => new Vector2(1, 2)[i]; }", false)]
    [InlineData("class C { public int M() => new[] { 1, 2, 3 }[10]; }", false)]
    public async Task InvalidVectorOrMatrixIndexerAnalyzer_Should_NotFlag_AnInBoundsIndex(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidVectorOrMatrixIndexerAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidVectorOrMatrixIndexerAnalyzer.DiagnosticId));
    }

    // ── Three hundred and second batch (триста семьдесят восьмой проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.Http.Headers; class C { public AuthenticationHeaderValue M() => AuthenticationHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public AuthenticationHeaderValue M() => AuthenticationHeaderValue.Parse(\"Bearer token\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public CacheControlHeaderValue M() => CacheControlHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public CacheControlHeaderValue M() => CacheControlHeaderValue.Parse(\"no-cache\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public ContentDispositionHeaderValue M() => ContentDispositionHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public ContentDispositionHeaderValue M() => ContentDispositionHeaderValue.Parse(\"attachment\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public ContentRangeHeaderValue M() => ContentRangeHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public ContentRangeHeaderValue M() => ContentRangeHeaderValue.Parse(\"bytes 0-100/200\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public EntityTagHeaderValue M() => EntityTagHeaderValue.Parse(\"no-quotes\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public EntityTagHeaderValue M() => EntityTagHeaderValue.Parse(\"\\\"abc\\\"\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public MediaTypeHeaderValue M() => MediaTypeHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public MediaTypeHeaderValue M() => MediaTypeHeaderValue.Parse(\"text/html\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public MediaTypeWithQualityHeaderValue M() => MediaTypeWithQualityHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public MediaTypeWithQualityHeaderValue M() => MediaTypeWithQualityHeaderValue.Parse(\"text/html;q=0.5\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public NameValueHeaderValue M() => NameValueHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public NameValueHeaderValue M() => NameValueHeaderValue.Parse(\"name=value\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public NameValueWithParametersHeaderValue M() => NameValueWithParametersHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public NameValueWithParametersHeaderValue M() => NameValueWithParametersHeaderValue.Parse(\"name=value\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public ProductHeaderValue M() => ProductHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public ProductHeaderValue M() => ProductHeaderValue.Parse(\"MyApp/1.0\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public ProductInfoHeaderValue M() => ProductInfoHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public ProductInfoHeaderValue M() => ProductInfoHeaderValue.Parse(\"MyApp/1.0\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public RangeConditionHeaderValue M() => RangeConditionHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public RangeConditionHeaderValue M() => RangeConditionHeaderValue.Parse(\"\\\"abc\\\"\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public RangeHeaderValue M() => RangeHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public RangeHeaderValue M() => RangeHeaderValue.Parse(\"bytes=0-100\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public RetryConditionHeaderValue M() => RetryConditionHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public RetryConditionHeaderValue M() => RetryConditionHeaderValue.Parse(\"120\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public StringWithQualityHeaderValue M() => StringWithQualityHeaderValue.Parse(\"???,,,\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public StringWithQualityHeaderValue M() => StringWithQualityHeaderValue.Parse(\"en-US\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public TransferCodingHeaderValue M() => TransferCodingHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public TransferCodingHeaderValue M() => TransferCodingHeaderValue.Parse(\"gzip\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public TransferCodingWithQualityHeaderValue M() => TransferCodingWithQualityHeaderValue.Parse(\"\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public TransferCodingWithQualityHeaderValue M() => TransferCodingWithQualityHeaderValue.Parse(\"gzip;q=0.5\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public ViaHeaderValue M() => ViaHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public ViaHeaderValue M() => ViaHeaderValue.Parse(\"1.1 proxy\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public WarningHeaderValue M() => WarningHeaderValue.Parse(\"???\"); }", true)]
    [InlineData("using System.Net.Http.Headers; class C { public WarningHeaderValue M() => WarningHeaderValue.Parse(\"199 - \\\"Miscellaneous warning\\\"\"); }", false)]
    [InlineData("using System.Net.Http.Headers; class C { public MediaTypeHeaderValue M(string s) => MediaTypeHeaderValue.Parse(s); }", false)]
    [InlineData("class C { public System.Guid M() => System.Guid.Parse(\"hello\"); }", false)]
    public async Task InvalidHttpHeaderValueLiteralAnalyzer_Should_NotFlag_AValidHeaderLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidHttpHeaderValueLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidHttpHeaderValueLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and third batch (триста семьдесят девятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"Narnia\"); }", true)]
    [InlineData("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"US\"); }", false)]
    [InlineData("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"en-US\"); }", false)]
    [InlineData("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"\"); }", true)]
    [InlineData("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"us\"); }", false)]
    [InlineData("using System.Globalization; class C { public RegionInfo M(string s) => new RegionInfo(s); }", false)]
    [InlineData("using System.Globalization; class C { public CultureInfo M() => new CultureInfo(\"Narnia\"); }", false)]
    public async Task InvalidRegionInfoLiteralAnalyzer_Should_NotFlag_AValidRegionLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidRegionInfoLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidRegionInfoLiteralAnalyzer.DiagnosticId));
    }

    // ── Three hundred and fourth batch (триста восьмидесятый проход) — one new analyzer.

    [Theory]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"no-slash-here\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"text/html\"); }", false)]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"   \"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"text\"); }", true)]
    [InlineData("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"text/html; charset=utf-8\"); }", false)]
    [InlineData("using System.Net.Mime; class C { public ContentType M(string s) => new ContentType(s); }", false)]
    [InlineData("class C { public System.Guid M() => System.Guid.Parse(\"hello\"); }", false)]
    public async Task InvalidContentTypeLiteralAnalyzer_Should_NotFlag_AValidContentTypeLiteral(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new InvalidContentTypeLiteralAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == InvalidContentTypeLiteralAnalyzer.DiagnosticId));
    }
}
