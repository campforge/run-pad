using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RunPad.Languages.CSharp.IntelliSense;

// The second of the two infrastructure
// blocks RoslynCodeFixEngine's own recursive-refactoring doc comment flagged as needed regardless.
// Runs Roslyn's own IDE-style DiagnosticAnalyzers (Microsoft.CodeAnalysis.CSharp.Features' IDE0xxx
// analyzers — "use var", "make field readonly", "remove unnecessary using", ...) alongside the
// compiler's own diagnostics. Most of these fire at Hidden severity BY DESIGN (no squiggle — only a
// CodeFixProvider keyed to that same diagnostic ID becomes available), which is exactly why several
// built-in CodeFixProviders RoslynCodeFixEngine already discovers were permanently silent before this:
// their triggering diagnostic never got computed when only the compiler's own diagnostics fed the
// lightbulb.
//
// Also reflects over THIS assembly (RunPad.Languages.CSharp),
// alongside Microsoft's own Features assembly, so a hand-written analyzer here (IntelliSense/
// Analyzers/) is discovered the same way, no separate registration list to keep in sync. First tenants:
// EmptyCatchClauseAnalyzer/DuplicateEnumValueAnalyzer — ported ideas from Roslynator (RCS1075/RCS1234,
// see that project's Apache-2.0-licensed source referenced in each file), not a dependency on it (written by hand,
// not pulled in as a package).
internal sealed class RoslynAnalyzerEngine
{
    // Roslyn's own IDE analyzers, and RunPad's (RPAD…), which the user can turn off.
    private readonly ImmutableArray<DiagnosticAnalyzer> _ideAnalyzers;
    private readonly ImmutableArray<DiagnosticAnalyzer> _runPadAnalyzers;
    private readonly ILogger _logger;

    public RoslynAnalyzerEngine(ILogger logger)
    {
        _logger = logger;

        // Not MEF (see RunPadRoslynHost.CSharpFeaturesAssembly's own doc comment for why) — plain
        // reflection over every concrete DiagnosticAnalyzer in the assembly, filtered to C# the same
        // way RoslynCodeFixEngine filters CodeFixProvider/CodeRefactoringProvider exports: reading the
        // language-applicability attribute directly rather than through Roslyn's own (internal)
        // metadata views.
        Type[] types;
        try { types = RunPadRoslynHost.CSharpFeaturesAssembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        Type[] ownTypes;
        try { ownTypes = typeof(RoslynAnalyzerEngine).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { ownTypes = ex.Types.Where(t => t != null).ToArray()!; }

        _ideAnalyzers = Construct(types);
        _runPadAnalyzers = Construct(ownTypes);
    }

    private ImmutableArray<DiagnosticAnalyzer> Construct(IEnumerable<Type> types)
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type)) continue;

            var attr = type.GetCustomAttribute<DiagnosticAnalyzerAttribute>();
            if (attr is null || !attr.Languages.Contains(LanguageNames.CSharp)) continue;

            // Every analyzer in this assembly has a parameterless constructor today (confirmed via a
            // live probe — 109/109 constructed cleanly); a future SDK version adding one that needs
            // MEF-imported dependencies would just fail this GetConstructor/Invoke and get skipped
            // rather than crash the whole engine over one analyzer.
            try
            {
                if (type.GetConstructor(Type.EmptyTypes) is not { } ctor) continue;
                analyzers.Add((DiagnosticAnalyzer)ctor.Invoke(null));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not construct analyzer {Analyzer}", type.Name);
            }
        }
        return analyzers.ToImmutable();
    }

    // Empty (not null) on failure — same "degrade gracefully, never take down the rest of the
    // background analysis pass" contract as RoslynCodeFixEngine's own materialization failures.
    public async Task<ImmutableArray<Diagnostic>> ComputeAsync(
        Compilation compilation, bool includeRunPadAnalyzers, CancellationToken ct)
    {
        var analyzers = includeRunPadAnalyzers ? _ideAnalyzers.AddRange(_runPadAnalyzers) : _ideAnalyzers;
        if (analyzers.IsEmpty) return [];

        try
        {
            var withAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
                new AnalyzerOptions([]),
                onAnalyzerException: (ex, analyzer, _) =>
                    _logger.LogDebug(ex, "Analyzer {Analyzer} threw", analyzer.GetType().Name),
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false));
            return await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);
        }
        catch (OperationCanceledException) { throw; } // routine — superseded by the next keystroke
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Analyzer pipeline failed");
            return [];
        }
    }
}
