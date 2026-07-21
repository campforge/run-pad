namespace DotPad.Contracts.IntelliSense;

// TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход, живой отчёт пользователя о
// подтормаживании интерфейса) — a single candidate quick fix/refactoring for the lightbulb margin.
// Deliberately carries NEITHER the applied text NOR the usings to import: materializing those
// (RoslynCodeFixEngine.TryMaterializeAsync — runs the fix provider's own document transform, then
// diffs it) turned out to be the dominant cost in the whole IntelliSense pipeline, paid for EVERY
// candidate from EVERY applicable provider on EVERY keystroke/caret-move, even though the user clicks
// at most one (often none) — measured ~25-50ms per GetRefactoringsAsync call and ~150ms-1.25s per
// background analysis tick on realistic script sizes, almost entirely from eagerly materializing
// fixes/refactorings nobody asked to see applied yet. Line/Title/Severity are all the lightbulb margin
// itself ever needed to draw the icon and build the menu (see CodeFixMargin) — Key is an opaque
// handle IIntelliSenseService.MaterializeFixAsync uses to do the expensive part JUST for the one item
// actually clicked, deferred until then. A key that's gone stale by click time (the underlying
// RoslynCodeFixEngine cache was replaced by a newer computation pass in the meantime) materializes to
// null — the same "gracefully do nothing" contract every other IntelliSense failure path in this
// codebase already follows, not a new failure mode to handle specially.
public interface ICodeFixInfo
{
    int Line { get; }
    string Title { get; }
    string Key { get; }

    // TODO.md #75: the lightbulb margin used to paint every fix with the same neutral color
    // regardless of whether it fixes a compiler error or a mere warning — this is the diagnostic's
    // own severity (Diagnostics.cs' DiagnosticSeverity, reused rather than a second enum), carried
    // through so the margin can color the icon accordingly.
    DiagnosticSeverity Severity { get; }

    // TODO.md #74 (сто шестой проход, user request 2026-07-21) — the Roslyn diagnostic ID this fix
    // is keyed to (e.g. "CS0184", "IDE0007"), for exact-match exclusion filtering by ID instead of a
    // fuzzy Title substring (a diagnostic ID is stable/documented; a title can be phrased however the
    // provider likes and isn't a contract). Null for a candidate with no underlying diagnostic — a
    // CodeRefactoringProvider's suggestion (Use Expression Body, Introduce Local, ...) is triggered by
    // caret position, not a diagnostic, so it simply can't be excluded by ID this way.
    string? DiagnosticId { get; }
}

// The expensive half of ICodeFixInfo, computed on demand (IIntelliSenseService.MaterializeFixAsync)
// only once the user actually clicks a specific candidate — see ICodeFixInfo's own doc comment for
// why this split exists. NewCode is the full document text after applying the fix's non-import
// changes; UsingsToAdd holds any namespaces the fix wants to import (e.g. Add Import) — per
// docs/ImplementationPlanRPad.md, `using` directives are project-level metadata (the .rpad front
// matter's `namespaces:` list, compiled in as `global using`), not text inserted into the visible
// script, so these are kept separate rather than folded into NewCode. A caller with no project file
// to route them to (e.g. a plain, non-.rpad script) can still fold them into the applied text itself.
public interface IMaterializedFix
{
    string NewCode { get; }
    IReadOnlyList<string> UsingsToAdd { get; }
}
