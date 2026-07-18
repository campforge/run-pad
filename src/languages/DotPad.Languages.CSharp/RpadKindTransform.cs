namespace DotPad.Languages.CSharp;

// Single source of truth for the literal text CSharpLanguageHost.ApplyKindTransform prepends for
// RpadKind.Program (ProgramMainCallPrefixFor(code) — "Main();" or "await Main();", no trailing
// newline either way — it's glued onto the same physical line as the user's first line, not a new
// one, which is why CalcKindOffset's Program case is 0, not 1 or 2; see ANALYSIS.md TODO #66).
// CSharpIntelliSenseService.SyncDocument mirrors this exact (code-dependent) prefix in its
// live-analysis document so the two can't drift apart again — without it, Roslyn correctly (from its
// own point of view) flagged the user's void Main() as an unused local function (CS8321), since the
// live-analysis document never actually contained a call to it.
internal static class RpadKindTransform
{
    public const string ProgramMainCallPrefix = "Main();";

    // TODO.md #63 (async Task Main): a Task-returning Main needs its call awaited, or its work (and
    // any exception thrown inside it) is silently discarded as a fire-and-forget call from a
    // top-level statement. Same length-invariant as ProgramMainCallPrefix — a single physical line,
    // no trailing '\n' — so CalcKindOffset's Program case (0) doesn't need to branch on which prefix
    // was actually used.
    public const string ProgramAsyncMainCallPrefix = "await Main();";

    public static string ProgramMainCallPrefixFor(string code) =>
        ProgramEntryPointDetector.MainReturnsTask(code) ? ProgramAsyncMainCallPrefix : ProgramMainCallPrefix;
}
