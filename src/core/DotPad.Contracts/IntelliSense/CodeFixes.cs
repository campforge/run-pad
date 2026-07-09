namespace DotPad.Contracts.IntelliSense;

// A single quick fix for a diagnostic. Line is 1-based, matching the editor's other line-indexed
// concepts (breakpoints, debug current line). NewCode is the full document text after applying the
// fix's non-import changes. UsingsToAdd holds any namespaces the fix wants to import (e.g. Add
// Import) — per docs/ImplementationPlanRPad.md, `using` directives are project-level metadata
// (the .rpad front matter's `namespaces:` list, compiled in as `global using`), not text inserted
// into the visible script, so these are kept separate rather than folded into NewCode. A caller with no
// project file to route them to (e.g. a plain, non-.rpad script) can still fold them into the
// applied text itself.
public interface ICodeFixInfo
{
    int Line { get; }
    string Title { get; }
    string NewCode { get; }
    IReadOnlyList<string> UsingsToAdd { get; }
}
