using System.Diagnostics;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;

namespace DotPad.UserControls.Models;

internal sealed class EditorTabState
{
    public CodeTextEditor Editor { get; }

    // Output panel state
    public string OutputContent { get; set; } = "";
    public string OutputCss { get; set; } = "";
    public bool HadRunError { get; set; }

    // Maps a DumpContainer's stable id (data-dp-id) to the exact wrapped block currently sitting in
    // OutputContent, so a later update from the same container can be found and replaced in place
    // instead of appended as a new block. Cleared at the start of each run.
    public Dictionary<string, string> DumpContainerBlocks { get; } = new();

    // Status bar state (restored when tab becomes active)
    public string ExecStatusText { get; set; } = "";
    public string ExecStatusColor { get; set; } = "";
    public string ElapsedText { get; set; } = "";

    // Run session
    public CancellationTokenSource? RunCts { get; set; }
    public ILanguageHost? RunningHost { get; set; }
    public IBreakpointHitContext? CurrentDebugCtx { get; set; }
    public readonly Stopwatch RunStopwatch = new();

    // Set when the active file is an .rpad document; null for untitled tabs
    public RpadDocument? RpadDocument { get; set; }

    // Compile output directory, reused across runs of this tab (allocated once, on first run).
    // Lets DotnetProcessCompiler skip NuGet restore when packages/references haven't changed —
    // see EditorTabItemControl.BuildRunOptions.
    public string? OutputDirectory { get; set; }

    // Path the currently-running (or most recently run) script was compiled from — set at the start
    // of RunAsync. Lets gutter breakpoint add/remove handlers construct a ScriptBreakpoint matching
    // what the debug session already knows about, while a debug session is active.
    public string? SourceFilePath { get; set; }

    public EditorTabState(CodeTextEditor editor)
    {
        Editor = editor;
    }
}
