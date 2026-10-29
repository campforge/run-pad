namespace Invoklet.UserControls.Models;

/// <summary>A breakpoint as the gutter shows it, without the debugger's verdict on it.</summary>
internal sealed record BreakpointState(int Line, string? Condition, string? HitCondition, bool IsEnabled);

/// <summary>
/// What a tab's editor shows beyond its text: where the caret and selection are, which line is at the
/// top and the breakpoints. Kept while the tab is unloaded and applied to the editor built again.
/// </summary>
internal sealed record EditorViewState(
    int CaretOffset, int SelectionStart, int SelectionLength, double TopLine, IReadOnlyList<BreakpointState> Breakpoints);
