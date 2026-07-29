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

    // ── Batch (сто сорок седьмой проход) — 1-2 tests per analyzer, per explicit user direction to
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

    // ── Second batch (сто сорок восьмой проход) — same "1-2 tests per analyzer" pace as проход 147.

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

    // ── Third batch (сто сорок девятый проход) — same pace as проходы 147/148.

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

    // ── Fourth batch (сто пятидесятый проход) — same pace as проходы 147/148/149.

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

    // ── Fifth batch (сто пятьдесят первый проход) — same pace as проходы 147-150.

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

    // ── Sixth batch (сто пятьдесят второй проход) — same pace as проходы 147-151.

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
}
