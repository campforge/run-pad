using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

/// <summary>
/// What the lightbulb margin offers: the fixes of diagnostics and the refactorings at the caret, two sources
/// that update independently (a refactoring has nothing to do with the diagnostics, so a text change elsewhere
/// does not recompute it). Each keeps its last list, so a change of the preferences can show the current lists
/// again at once instead of waiting for the next keystroke.
/// </summary>
internal sealed class LightbulbEntries
{
    private IReadOnlyList<CodeFixEntry> _diagnosticFixes = [];
    private IReadOnlyList<CodeFixEntry> _refactorings = [];

    public void SetDiagnosticFixes(IReadOnlyList<ICodeFixInfo> fixes) => _diagnosticFixes = ToEntries(fixes);

    /// <summary>Null, as the language service gives it when there is nothing at the caret, clears them.</summary>
    public void SetRefactorings(IReadOnlyList<ICodeFixInfo>? refactorings)
        => _refactorings = refactorings is null ? [] : ToEntries(refactorings);

    /// <summary>The entries to show: none when code fixes are switched off, otherwise both lists without the excluded diagnostics.</summary>
    public IReadOnlyList<CodeFixEntry> Visible(bool codeFixesEnabled, IReadOnlyCollection<string> excludedDiagnosticIds)
    {
        if (!codeFixesEnabled) return [];
        return
        [
            .. _diagnosticFixes.Concat(_refactorings).Where(f =>
                f.DiagnosticId is null || !excludedDiagnosticIds.Contains(f.DiagnosticId, StringComparer.OrdinalIgnoreCase)),
        ];
    }

    // Line, title, severity and key only: the fix itself is materialized for the one entry that is clicked.
    private static List<CodeFixEntry> ToEntries(IReadOnlyList<ICodeFixInfo> fixes)
        => fixes.Select(f => new CodeFixEntry(f.Line, f.Title, ToMarginSeverity(f.Severity), f.Key, f.DiagnosticId)).ToList();

    // The margin's CodeFixSeverity duplicates DiagnosticSeverity because the control does not reference the
    // contracts; this is the one place the two enums are mapped.
    private static CodeFixSeverity ToMarginSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => CodeFixSeverity.Error,
        DiagnosticSeverity.Warning => CodeFixSeverity.Warning,
        _ => CodeFixSeverity.Info,
    };
}
