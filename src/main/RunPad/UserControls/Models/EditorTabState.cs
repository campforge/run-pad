using System.Diagnostics;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;

namespace RunPad.UserControls.Models;

internal sealed class EditorTabState
{
    public CodeTextEditor Editor { get; }

    // Output panel state — bound directly to OutputHtmlView's DataContext (see
    // EditorsTabItemControl's constructor), so setting Output.Content/Output.Css here is itself the
    // "please redraw" signal; nothing here needs to also call back into the view.
    public OutputHtmlViewModel Output { get; } = new();
    public bool HadRunError { get; set; }

    // Maps a DumpContainer's stable id (data-dp-id) to the exact wrapped block currently sitting in
    // Output.Content, so a later update from the same container can be found and replaced in place
    // instead of appended as a new block. Cleared at the start of each run.
    public Dictionary<string, string> DumpContainerBlocks { get; } = new();

    // Status bar state (restored when tab becomes active)
    public string ExecStatusText { get; set; } = "Ready";
    public string ExecStatusColor { get; set; } = "RunPad.Text.SecondaryBrush";
    public string ElapsedText { get; set; } = "";

    // Run session
    public CancellationTokenSource? RunCts { get; set; }
    public ILanguageHost? RunningHost { get; set; }
    public IBreakpointHitContext? CurrentDebugCtx { get; set; }

    // The unhandled exception the current debug run stopped on: continuing from it ends the script
    // with that exception, which netcoredbg still reports as exit code 0 — so the run's end status
    // comes from here instead of looking like a success.
    public DebugExceptionInfo? UnhandledDebugException { get; set; }
    public readonly Stopwatch RunStopwatch = new();

    // Set when the active file is an .rpad document; null for untitled tabs
    public RpadDocument? RpadDocument { get; set; }

    // Set when the file on disk isn't a valid .rpad document (RpadParser.Parse error): the
    // editor then holds the full raw file so Save writes it back verbatim, and Run/Debug are refused.
    public string? DocumentParseError { get; set; }

    // Compile output directory, reused across runs of this tab (allocated once, on first run).
    // Lets the script compiler skip NuGet restore and recompiling when nothing changed —
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
