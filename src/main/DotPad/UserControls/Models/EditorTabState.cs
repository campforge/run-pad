using System.Diagnostics;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Contracts;
using DotPad.Contracts.Models;

namespace DotPad.UserControls.Models;

internal sealed class EditorTabState
{
    public CodeTextEditor Editor { get; }

    // Output panel state
    public string OutputContent { get; set; } = "";
    public string OutputCss { get; set; } = "";
    public bool HadRunError { get; set; }

    // Status bar state (restored when tab becomes active)
    public string ExecStatusText { get; set; } = "";
    public string ExecStatusColor { get; set; } = "";
    public string ElapsedText { get; set; } = "";

    // Run session
    public CancellationTokenSource? RunCts { get; set; }
    public ILanguageHost? RunningHost { get; set; }
    public IBreakpointHitContext? CurrentDebugCtx { get; set; }
    public readonly Stopwatch RunStopwatch = new();

    public EditorTabState(CodeTextEditor editor)
    {
        Editor = editor;
    }
}
