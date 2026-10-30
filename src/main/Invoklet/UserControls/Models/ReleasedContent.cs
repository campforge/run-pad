namespace Invoklet.UserControls.Models;

/// <summary>Where a tab's editor has the caret and selection, and which line is at the top.</summary>
public sealed record EditorViewState(int CaretOffset, int SelectionStart, int SelectionLength, double TopLine);

/// <summary>What a tab's last run left in its output panel.</summary>
public sealed record TabOutputState(string Content, bool HadRunError, IReadOnlyDictionary<string, string> DumpContainerBlocks);

/// <summary>
/// The heavy part of a tab that is not being used: its text and its output. They wait on disk while the
/// control, which is light, stays as it is. The output is here only when it has no folder of its run to wait in.
/// </summary>
public sealed record ReleasedContent(string Text, EditorViewState Editor, TabOutputState? Output);
