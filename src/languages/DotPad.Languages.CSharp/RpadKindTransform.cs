namespace DotPad.Languages.CSharp;

// Single source of truth for the literal text CSharpLanguageHost.ApplyKindTransform prepends for
// RpadKind.Program ("Main();", no trailing newline — it's glued onto the same physical line as the
// user's first line, not a new one, which is why CalcKindOffset's Program case is 0, not 1 or 2; see
// ANALYSIS.md TODO #66). CSharpIntelliSenseService.SyncDocument mirrors this exact prefix in its
// live-analysis document so the two can't drift apart again — without it, Roslyn correctly (from its
// own point of view) flagged the user's void Main() as an unused local function (CS8321), since the
// live-analysis document never actually contained a call to it.
internal static class RpadKindTransform
{
    public const string ProgramMainCallPrefix = "Main();";
}
