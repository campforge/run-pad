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

    // ── Seventh batch (сто пятьдесят третий проход) — same pace as проходы 147-152.

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

    // ── Eighth batch (сто пятьдесят четвёртый проход) — same pace as проходы 147-153.

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

    // ── Ninth batch (сто пятьдесят пятый проход) — same pace as проходы 147-154.

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

    // ── Tenth batch (сто пятьдесят шестой проход) — same pace as проходы 147-155.

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

    // ── Eleventh batch (сто пятьдесят седьмой проход) — same pace as проходы 147-156.

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

    // ── Twelfth batch (сто пятьдесят восьмой проход) — same pace as проходы 147-157.

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

    // ── Thirteenth batch (сто пятьдесят девятый проход) — 3 new analyzers + 3 extensions to earlier
    // ones, same pace as проходы 147-158.

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

    // ── Fourteenth batch (сто шестидесятый проход) — same pace as проходы 147-159.

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
    public async Task RedundantDefaultComparerArgumentAnalyzer_Should_NotFlag_ADirectCall(string code, bool expectFlagged)
    {
        var diagnostics = await GetDiagnosticsAsync(new RedundantDefaultComparerArgumentAnalyzer(), code);
        Assert.Equal(expectFlagged, diagnostics.Any(d => d.Id == RedundantDefaultComparerArgumentAnalyzer.DiagnosticId));
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
}
