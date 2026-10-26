using System.Reactive.Linq;
using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;
using RunPad.Resources;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.Services.Recovery;
using RunPad.Services.Scaling;
using RunPad.UserControls.Models;
using TextMateSharp.Grammars;
using ContractSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.UserControls;

public partial class EditorsTabItemControl : UserControl, IDisposable
{
    private readonly ILanguageHostRegistry _registry;
    private readonly ConnectionDriverRegistry _connectionDrivers;
    private readonly ConnectionSchemas? _connectionSchemas;
    private readonly IPreferencesService _prefs;
    private readonly EditorTabItemViewModel _vm;
    private TabControl? _outputTabsControl;

    private readonly IThemeService _themeService;
    private readonly IWindowService _windowService;
    internal readonly EditorTabState State;
    private SemanticHighlighter? _semanticHighlighter;
    private CancellationTokenSource? _debugTooltipCloseCts;
    private bool _debugTooltipInside;
    private InlayHintRenderer? _inlayHintRenderer;
    private IIntelliSenseService? _intelliSense;
    internal IIntelliSenseService? IntelliSenseForTests => _intelliSense;
    private bool _runPadAnalyzers;
    private bool _resultsWordWrap;

    // Raw (unfiltered) lightbulb candidate lists — see UpdateLightbulb's own doc comment for why
    // filtering happens at render time instead of once when each list is produced.
    private List<CodeFixEntry> _diagnosticFixEntries = [];
    private List<CodeFixEntry> _refactoringFixEntries = [];
    private long _inputPromptRevision;
    private readonly IBuildCacheService _buildCache;
    private IDisposable? _selectedKindSub;
    private IDisposable? _filePathSub;
    private ActiveTheme? _pendingTheme;
    // A Run/Debug waiting for its save (possibly for the user's answer): another one doesn't start.
    private bool _savingBeforeRun;
    private string? _filePath;
    private IDisposable? _selectedSdkSub;
    private bool _disposed;
    private bool _runInProgress;
    private readonly DebugHoverController _debugHover = new();

    // The stage the current (or last) run reached, -1 before the first: names the stage that failed.
    // Written as the host reports it, on whichever thread that is, so it is already current when the
    // run ends even if the status update posted for it has not been processed yet.
    private int _runStageValue = -1;

    private RunStage? RunStageReached => _runStageValue < 0 ? null : (RunStage)Volatile.Read(ref _runStageValue);
    private Task? _activeRun;
    private bool _restarting;
    private bool _restartRequested;
    private int _runGeneration;
    private int _stopGeneration;
    private readonly ILogger<EditorsTabItemControl> _runLogger;

    // Front matter as of the last point it was accounted for (loaded, or already flagged dirty) —
    // see OnDependenciesEdited. Not "what's on disk": a failed write keeps the tab dirty instead.
    private string _metadataBaseline = "";

    // The code zoom shared by every editor of the app (and kept in the preferences).
    private readonly ViewScale _viewScale;

    // Debounce before firing a Quick Info request after the pointer stops moving — long enough
    // that just passing over a symbol doesn't spam requests, short enough to still feel responsive
    //.
    private static readonly TimeSpan QuickInfoHoverDelay = TimeSpan.FromMilliseconds(400);

    // Same rationale as QuickInfoHoverDelay, for refactorings — caret moves far more
    // often than the pointer stops over a symbol (every arrow key press, every click), so this needs
    // the same debounce discipline diagnostics/inlay hints already get from NotifyTextChanged's own
    // 500ms — refactorings just can't share that pass (see GetRefactoringsAsync's own doc comment on
    // IIntelliSenseService: availability depends on caret position, not on what changed in the text).
    private static readonly TimeSpan RefactoringDebounceDelay = TimeSpan.FromMilliseconds(400);

    // Background callbacks (these debounces, IntelliSense events, run-host output) marshal back via
    // this control's own Dispatcher (AvaloniaObject.Dispatcher, captured at construction), never the
    // static Dispatcher.UIThread: that static is created lazily by whichever thread reads it first
    // once unset — after Avalonia.Headless resets it between isolated tests, a late callback from a
    // finished test made a thread-pool thread "the UI thread" and the next test's app setup failed
    // VerifyAccess (DefaultRenderLoop.Add). Same dispatcher in the real app; no behavior change there.

    internal EditorsTabItemControl(
        EditorTabItemViewModel vm,
        IThemeService themeService,
        IWindowService windowService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IIntelliSenseService? intelliSense,
        IBuildCacheService buildCache, ILogger<EditorsTabItemControl>? runLogger = null,
        ConnectionDriverRegistry? connectionDrivers = null, ConnectionSchemas? connectionSchemas = null)
    {
        _connectionDrivers = connectionDrivers ?? new ConnectionDriverRegistry();
        _connectionSchemas = connectionSchemas;
        if (connectionSchemas is not null) connectionSchemas.Cache.Changed += OnConnectionSchemaChanged;
        _themeService = themeService;
        _windowService = windowService;
        _registry = registry;
        _prefs = prefs;
        _buildCache = buildCache;
        _runLogger = runLogger ?? NullLogger<EditorsTabItemControl>.Instance;
        _vm = vm;
        _vm.WriteToFile = WriteFile;
        DataContext = _vm;

        InitializeComponent();

        _outputTabsControl = this.FindControl<TabControl>("OutputTabsControl");
        var editor = new CodeTextEditor { Background = _themeService.GetBrush("RunPad.Editor.BackgroundBrush") };
        State = new EditorTabState(editor);
        ResultsOutput.DataContext = State.Output;
        _resultsWordWrap = prefs.Editor.ResultsWordWrap;
        State.Output.Css = HtmlThemeStylesheet.Generate(themeService.CurrentTheme.Interface.Variant,
            themeService.CurrentTheme.Interface.IsDark, _resultsWordWrap);
        // Results wrapping is set in the output's own settings menu, not in Preferences.
        ResultsWrapCheckBox.IsChecked = _resultsWordWrap;
        ResultsWrapCheckBox.IsCheckedChanged += OnResultsWrapCheckBoxChanged;
        OutputSettingsButton.Flyout!.Opened += (_, _) => ResultsWrapCheckBox.IsChecked = _prefs.Editor.ResultsWordWrap;

        _viewScale = ViewScale.For(prefs);
        ApplyEditorSettings(editor, prefs.Editor, _viewScale.CodeZoom);
        prefs.Keyboard.ApplyTo(editor);
        prefs.Changed += OnPreferencesChanged;

        // Ctrl+wheel, the status-bar zoom menu and View → Zoom all change the one shared code zoom;
        // every editor then follows it in OnViewScaleChanged.
        editor.ZoomRequested += OnEditorZoomRequested;
        _viewScale.Changed += OnViewScaleChanged;
        UpdateZoomButton();

        _intelliSense = intelliSense;
        _intelliSense?.UpdateIndentation(prefs.Editor.UseTabs, prefs.Editor.IndentationSize);
        _runPadAnalyzers = prefs.Experimental?.RunPadAnalyzers == true;
        intelliSense?.UpdateRunPadAnalyzers(_runPadAnalyzers);
        SetupEditor(editor, intelliSense);
        editor.BreakpointAdded += OnGutterBreakpointAdded;
        editor.BreakpointRemoved += OnGutterBreakpointRemoved;

        _selectedKindSub = _vm.WhenAnyValue(x => x.SelectedKind).Subscribe(OnSelectedKindChanged);
        _filePath = _vm.FilePath;
        _filePathSub = _vm.WhenAnyValue(x => x.FilePath).Skip(1).Subscribe(OnFilePathChanged);
        // Another target framework can mean other compile assets of the same packages.
        _selectedSdkSub = _vm.WhenAnyValue(x => x.SelectedSdk).Skip(1).Subscribe(OnSelectedSdkChanged);

        // Load document content first, without TextMate.
        // SetLanguage calls Redraw() internally, which crashes when the editor
        // is not yet in the visual tree (no visual lines exist yet).
        // We defer SetLanguage to AttachedToVisualTree so Redraw() works on the
        // first call and highlighting appears immediately, without NullReferenceException.
        SetDocumentContent(editor, _vm.FilePath);

        // Dirty indicator ("tab lifecycle") — attached only AFTER the initial
        // SetDocumentContent call above, not before: setting editor.Document there fires TextChanged
        // synchronously (see the comment inside SetDocumentContent on RefreshIntelliSenseKind's
        // ordering for the same fact), which would otherwise flag a just-opened, untouched tab as
        // dirty on its very first frame — including a brand-new empty tab, which has nothing
        // unsaved yet until the user actually types something.
        editor.TextChanged += (_, _) => _vm.IsDirty = true;

        // Snippets and the editor's lexical/syntactic profile are both per-language (see
        // ILanguageHostFactory.Snippets/EditorProfile) — populated here rather than in SetupEditor
        // because SetupEditor runs before SetDocumentContent, when ResolveLanguageId can't yet see
        // the real file's language and would default to C#.
        foreach (var (keyword, template) in _registry.GetSnippets(ResolveLanguageId()) ??
                                            new Dictionary<string, string>())
            editor.Snippets[keyword] = template;
        ApplyEditorLanguageProfile(editor,
            _registry.GetEditorProfile(ResolveLanguageId()) ?? new EditorLanguageProfile());

        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (s, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            // The grammar of the tab's language (untitled tabs default to C#, see ResolveLanguageId);
            // plain text when its profile names no source extension the editor has a grammar for.
            // The palette current when the tab is first shown — not the one when it was created: a
            // tab restored at startup may only be opened after the theme changed.
            editor.TrySetLanguage(_themeService!.CurrentTheme.Editor.TextMateName,
                _registry.GetEditorProfile(ResolveLanguageId())?.SourceFileExtension ?? "");
            ApplySelectionColor(editor);
        };
        editor.AttachedToVisualTree += onAttached;

        // Caret position → StatusLine
        editor.TextArea.Caret.PositionChanged += (_, _) =>
            StatusLine.Text = $"Line {editor.TextArea.Caret.Line}  Col {editor.TextArea.Caret.Column}";

        EditorHost.Content = editor;
        // The IL tab's content only joins the visual tree when the tab is first shown.
        IlOutput.AttachedToVisualTree += (_, _) => EnsureIlHighlighting();
        SetupPreview();
        _vm.CaptureRecovery = CaptureRecovery;
        _vm.RecoveryReady.TrySetResult();

        // Watch panel: re-evaluate an expression whenever its text changes (row added via
        // AddWatchCommand, or an existing row edited), and every row when a new breakpoint hit
        // gives us a fresh IBreakpointHitContext (see OnBreakpointHit).
        _vm.DebugPanel.WatchExpressions.CollectionChanged += OnWatchExpressionsCollectionChanged;
    }

    // Named (not an anonymous lambda) so Dispose can unsubscribe it — this control and
    // its ViewModel are always created/destroyed as a pair today (EditorTabsControl.cs), so it isn't
    // an active leak yet, but leaving it unpaired with the rest of Dispose's own subscriptions
    // (_prefs.Changed/_selectedKindSub) would turn into a real one the moment that assumption changes.
    private void OnWatchExpressionsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (WatchExpression w in e.NewItems)
            {
                w.PropertyChanged += OnWatchExpressionPropertyChanged;
                _ = EvaluateWatchAsync(w);
            }

        if (e.OldItems is not null)
            foreach (WatchExpression w in e.OldItems)
                w.PropertyChanged -= OnWatchExpressionPropertyChanged;
    }

    private void OnWatchExpressionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WatchExpression.Expression)) return;
        if (sender is WatchExpression w) _ = EvaluateWatchAsync(w);
    }

    private async Task EvaluateWatchAsync(WatchExpression w)
    {
        if (string.IsNullOrWhiteSpace(w.Expression))
        {
            w.Value = "?";
            w.TypeName = "";
            return;
        }

        if (State.CurrentDebugCtx is not { } ctx)
        {
            w.Value = "<not paused>";
            w.TypeName = "";
            return;
        }

        try
        {
            var result = await ctx.EvaluateAsync(w.Expression);
            w.Value = result?.Value ?? "<error>";
            w.TypeName = result?.TypeName ?? "";
        }
        catch (Exception ex)
        {
            w.Value = $"<error: {ex.Message}>";
            w.TypeName = "";
        }
    }

    // A tab that isn't shown (another tab is selected, so it is out of the visual tree) only keeps
    // the latest theme, and applies it when it is shown again, before it is drawn: hidden editors
    // aren't recoloured on every switch, and the one the user returns to has the current palette.
    internal void ApplyTheme(ActiveTheme theme)
    {
        if (VisualRoot is null)
        {
            _pendingTheme = theme;
            return;
        }
        _pendingTheme = null;
        ApplyThemeNow(theme);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_pendingTheme is { } theme)
        {
            _pendingTheme = null;
            ApplyThemeNow(theme);
        }
    }

    // Test-only (InternalsVisibleTo): the theme waiting for the tab to be shown.
    internal ActiveTheme? PendingThemeForTests => _pendingTheme;

    private void ApplyThemeNow(ActiveTheme theme)
    {
        State.Editor.SetTheme(theme.Editor.TextMateName);
        _ilHighlighting?.SetTheme(theme.Editor.TextMateName);
        _semanticHighlighter?.SetDark(theme.Editor.IsDark);
        var editorBg = _themeService!.GetBrush("RunPad.Editor.BackgroundBrush");

        if (editorBg is not null)
        {
            State.Editor.Background = editorBg;
        }

        ApplySelectionColor(State.Editor);
        State.Output.Css = HtmlThemeStylesheet.Generate(theme.Interface.Variant, theme.Interface.IsDark, _resultsWordWrap);
        UpdateOutputUi();
        ApplyPreviewThemeNow(theme);
    }

    // Deliberately a no-op for a tab with no FilePath yet ("tab lifecycle") — an
    // untitled, never-saved tab has nowhere to write to; it only gets a path (and starts being
    // covered by this method) via Save As (MainWindowViewModel.SaveTabAsAsync), never implicitly.
    internal void SaveIfNeeded() => _ = SaveAsync();

    // Explicit Save and in-place metadata edits (tab close/app exit/rename resolve unsaved content
    // themselves first). A file changed by another program is only replaced on the user's choice
    // (InPlaceSave). A write failure (disk full, permissions, file locked externally) and a declined
    // save are both shown in the status strip RunAsync uses, so the user knows the file wasn't
    // written; either way the tab stays unsaved.
    internal async Task SaveAsync() => await SaveWithResultAsync();

    private async Task<InPlaceSaveResult> SaveWithResultAsync()
    {
        if (_vm.FilePath is null) return InPlaceSaveResult.Cancelled;
        var result = await InPlaceSave.SaveAsync(_vm, _windowService, async () => await _vm.SaveAsCommand.Execute());
        if (_disposed) return result;
        if (result.Status == InPlaceSaveStatus.Failed)
            SetStatus($"Save failed: {result.Error.Replace('\n', ' ')}", "RunPad.Status.ErrorBrush");
        else if (result.Status == InPlaceSaveStatus.Cancelled && _vm.IsDirty)
            SetStatus("Not saved: the file was changed by another program", "RunPad.Status.CancelledBrush");
        UpdateOutputUi();
        return result;
    }

    // internal (not private): reused by MainWindowViewModel.SaveTabAsAsync/DuplicateTab (via
    // EditorTabsControl, which wires the tab's SaveAsCommand/DuplicateCommand to it) to write this
    // tab's current content to a path other than _vm.FilePath — the exact same serialization
    // SaveIfNeeded already uses for the normal save path, just parameterized on the target path.
    // The bytes written are remembered (EditorTabItemViewModel.LastWrite) so a later in-place save
    // can tell its own last write apart from another program's change.
    internal void WriteFile(string path)
    {
        byte[] bytes;
        if (State.RpadDocument is { } rpad)
        {
            rpad.RunCode = State.Editor.Text;
            // Stable document GUID (G07): a legacy file without an ID receives one on first save;
            // Save As / Duplicate (path differs from the tab's own file) creates a new identity.
            var isSaveAs = !string.Equals(path, _vm.FilePath, StringComparison.Ordinal);
            if (rpad.FrontMatter.Id is null || isSaveAs)
                rpad.FrontMatter.Id = Guid.NewGuid();
            // A script written to another folder (Save As) gets its relative references rewritten so
            // they still reach the same files; the tab adopts them only once it really moved there
            // (OnFilePathChanged) — a cancelled or failed Save As leaves its references as they were.
            var references = rpad.FrontMatter.References;
            if (references is not null && RebasedReferences(references, _vm.ReferenceAnchorPath, path) is { } rebased)
                rpad.FrontMatter.References = rebased;
            try
            {
                // As File.WriteAllText(path, text, Encoding.UTF8) did: with the UTF-8 byte order mark.
                bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(RpadWriter.Write(rpad))];
            }
            finally
            {
                rpad.FrontMatter.References = references;
            }
        }
        else
        {
            // As File.WriteAllText(path, text) did: UTF-8 without a byte order mark.
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(State.Editor.Text);
        }
        File.WriteAllBytes(path, bytes);
        _vm.LastWrite = (path, ScriptFileState.Of(bytes));
    }

    // The references as written for the script saved at newPath instead of oldPath (its own file, or
    // the one an untitled copy was made from), or null if nothing changes (same folder, an untitled
    // tab whose relative references had no folder yet, or nothing relative).
    private static List<string>? RebasedReferences(List<string> references, string? oldPath, string newPath)
    {
        if (oldPath is null) return null;
        var oldDirectory = Path.GetDirectoryName(Path.GetFullPath(oldPath))!;
        var newDirectory = Path.GetDirectoryName(Path.GetFullPath(newPath))!;
        var rebased = references.Select(r => ReferencePath.Rebase(r, oldDirectory, newDirectory, OperatingSystem.IsWindows())).ToList();
        return rebased.SequenceEqual(references, StringComparer.Ordinal) ? null : rebased;
    }

    // Save As rebinds the tab to its new file: the references written there become its own, and
    // IntelliSense resolves relative ones against the new folder — also the first Save As of an
    // untitled tab, whose relative references only now have a folder (nothing to rebase there) — and
    // checks the unchanged text again at once, as nothing else would until the next edit.
    private void OnFilePathChanged(string? newPath)
    {
        var oldPath = _filePath;
        _filePath = newPath;
        if (_disposed || newPath is null || oldPath == newPath) return;
        // An untitled copy's references were relative to the file it was made from.
        var anchor = oldPath ?? _vm.RestoredRecovery?.OriginalPath;
        if (anchor is not null
            && State.RpadDocument?.FrontMatter is { References: { } references } frontMatter
            && RebasedReferences(references, anchor, newPath) is { } rebased)
        {
            frontMatter.References = rebased;
            _metadataBaseline = FrontMatterSnapshot();
        }
        RefreshIntelliSenseContext(reanalyze: true);
        UpdatePreviewNow();
    }

    internal void Cancel()
    {
        _restartRequested = false;
        _stopGeneration++;
        HideInputPrompt();
        try
        {
            State.CurrentDebugCtx?.Resume(DebugAction.Continue);
            _debugHover.Cancel();
            State.Editor.ClearHoverTokenHighlight();
            ToolTip.SetTip(State.Editor.TextArea.TextView, null);
            State.CurrentDebugCtx = null;
            _vm.DebugPanel.ClearStop();
            State.RunCts?.Cancel();
        }
        catch (Exception ex)
        {
            _runLogger.LogError(ex, "Stopping the script failed");
            if (!_disposed) AppendError(ex);
        }
    }

    // All UI entry points await this boundary, including errors before execution and in cleanup.
    internal Task RunAsync(bool debug, DotnetSdk platform)
    {
        if (_activeRun is { IsCompleted: false }) return Task.CompletedTask;
        return _activeRun = RunSafeAsync(debug, platform, null);
    }

    internal Task RunSelectionAsync(DotnetSdk platform)
    {
        if (_disposed || _restarting || _activeRun is { IsCompleted: false }) return Task.CompletedTask;
        var editor = State.Editor;
        if (string.IsNullOrWhiteSpace(editor.SelectedText)) return Task.CompletedTask;
        // Statements and Program scripts run the selected statements on their own, as Statements:
        // in a Program the selection is usually the body of Main or another method, which compiles as
        // top-level statements once the rest is blanked out. An Expression script has no statements.
        if ((State.RpadDocument?.FrontMatter.Kind ?? _vm.NewFileKind) == RpadKind.Expression)
        {
            SetStatus("Execute Selection is not available in Expression mode", "RunPad.Status.ErrorBrush");
            UpdateOutputUi();
            return Task.CompletedTask;
        }
        // Rectangular selections contain separate segments; SelectedText includes unselected gaps.
        // Mask those gaps while preserving every original source offset and line ending.
        var segments = editor.TextArea.Selection.Segments.ToArray();
        if (segments.Length == 0) return Task.CompletedTask;
        var selected = editor.Text.Take(segments.Max(segment => segment.EndOffset))
            .Select(c => c is '\r' or '\n' ? c : ' ').ToArray();
        foreach (var segment in segments)
            editor.Text.CopyTo(segment.StartOffset, selected, segment.StartOffset, segment.Length);
        var script = new string(selected);
        if (string.IsNullOrWhiteSpace(script)) return Task.CompletedTask;
        return _activeRun = RunSafeAsync(false, platform, script);
    }

    internal async Task RestartAsync(DotnetSdk platform)
    {
        if (_disposed || _restarting) return;
        _restarting = true;
        try
        {
            var previous = _activeRun;
            if (previous is { IsCompleted: false })
            {
                Cancel();
                _restartRequested = true;
                SetStatus("Restarting…", "RunPad.Status.RunningBrush");
                UpdateOutputUi();
                await previous;
                if (!_restartRequested) return;
            }
            _restartRequested = false;
            if (!_disposed) await RunAsync(_vm.DebugModeEnabled, platform);
        }
        finally { _restarting = false; _restartRequested = false; }
    }

    private async Task RunSafeAsync(bool debug, DotnetSdk platform, string? selection)
    {
        try { await RunCoreAsync(debug, platform, selection); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            _runLogger.LogError(ex, "Script execution failed");
            if (!_disposed)
            {
                _vm.RunState = RunState.Idle;
                AppendError(ex);
            }
        }
    }

    private async Task RunCoreAsync(bool debug, DotnetSdk platform, string? selection)
    {
        var s = State;
        if (_disposed || _runInProgress || _savingBeforeRun) return;
        var stopGeneration = _stopGeneration;

        // Never run an invalid .rpad as raw code — the editor holds the whole file
        // (front matter + Markdown) there, not a script.
        if (s.DocumentParseError is { } parseError)
        {
            s.Output.Clear();
            s.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(InvalidDocumentMessage(parseError))}</div>");
            SetStatus("Invalid document", "RunPad.Status.ErrorBrush");
            UpdateOutputUi();
            return;
        }

        // A script is saved before it runs, like any save: a file changed by another program is only
        // replaced on the user's choice, and a save that doesn't happen (cancelled, a Save As that
        // didn't save, a failed write) means no run — nothing below has started yet. A Save As that
        // did save runs the script from its new file. An untitled script runs without being saved.
        if (selection is null && _vm.FilePath is { } path)
        {
            InPlaceSaveResult saved;
            _savingBeforeRun = true;
            try
            {
                saved = await SaveWithResultAsync();
            }
            finally
            {
                _savingBeforeRun = false;
            }
            if (_disposed || _restartRequested || stopGeneration != _stopGeneration) return;
            if (saved.Status == InPlaceSaveStatus.Failed)
            {
                s.Output.Clear();
                AppendError(new IOException($"Cannot save '{Path.GetFileName(path)}': {saved.Error.Replace('\n', ' ')}"));
                UpdateOutputUi();
                return;
            }
            if (saved.Status != InPlaceSaveStatus.Saved) return;
        }

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.Output.Clear();
        s.DumpContainerBlocks.Clear();
        s.HadRunError = false;
        s.UnhandledDebugException = null;
        Volatile.Write(ref _runStageValue, -1);
        _vm.DebugPanel.ClearStop();
        s.ElapsedText = "";
        // The IL of the last build stays until a new one replaces it (Compiled): a run that fails to
        // compile or start has no IL of its own and must not take the previous one away.
        SetStatus("Process", "RunPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        if (s.RpadDocument is { } doc && TargetSdkResolver.Warning(doc.FrontMatter.TargetSdk, platform) is { } sdkWarning)
            s.Output.Append($"\n<div>warning: {HttpUtility.HtmlEncode(sdkWarning)}</div>");
        UpdateOutputUi();

        s.RunCts?.Dispose();
        var runCts = new CancellationTokenSource();
        s.RunCts = runCts;
        _runInProgress = true;
        var generation = ++_runGeneration;
        void PostRun(Action action) => Dispatcher.Post(() =>
        {
            if (!_disposed && generation == _runGeneration) action();
        });

        var cancelled = false;
        RunResult? runResult = null; // stays null for a debug run — DAP doesn't report an exit code here yet
        try
        {
            var workDir = EnsureWorkDirectory();
            // A gRPC server's classes are part of the build: read the ones not read yet, and try
            // again those that failed (the server may be up now).
            if (_connectionSchemas is { } schemas && s.RpadDocument?.FrontMatter.Connections is { Count: > 0 } connections)
            {
                // Off the UI thread: a driver may read the whole schema synchronously (SQLite does).
                List<RpadConnection> list = [.. connections];
                var scriptDirectory = ScriptDirectory;
                var token = runCts.Token;
                Volatile.Write(ref _runStageValue, (int)RunStage.ReadingConnectionSchemas);
                ShowRunStage(RunStage.ReadingConnectionSchemas, null);
                await Task.Run(() => schemas.EnsureAsync(list, scriptDirectory, ConnectionSchemaRefresh.Failed, token), token);
            }
            var request = BuildRunRequest(platform, workDir);
            // A selection runs as Statements whatever the script's own kind (see RunSelectionAsync).
            if (selection is not null) request = request with { Kind = RpadKind.Statements };
            var script = selection ?? s.Editor.Text;
            // Read on the UI thread; they name the host's source file once it exists (see below).
            var enabledBreakpoints = s.Editor.Breakpoints
                .Where(bp =>
                    bp.IsEnabled) // Disabled breakpoints stay in the gutter but never reach the debugger
                .Select(bp => (bp.Line, bp.Condition, bp.HitCondition))
                .ToList();
            // Reset to Pending — stale Verified/Unverified color from a previous run shouldn't linger
            // in the margin until the new session's setBreakpoints response arrives.
            foreach (var bp in s.Editor.Breakpoints)
            {
                bp.VerificationState = BreakpointVerificationState.Pending;
            }

            s.Editor.InvalidateBreakpoints();

            await Task.Run(async () =>
            {
                await using var host = _registry.Create(ResolveLanguageId(), request);
                s.RunningHost = host;
                // The file the host compiles and its debugger reports (see ScriptBreakpoint).
                var sourceFilePath = host.SourceFilePath ?? "";
                s.SourceFilePath = sourceFilePath;
                host.Compiled += binaryPath =>
                {
                    if (_disposed || generation != _runGeneration) return;
                    UpdateIlOutput(binaryPath, generation);
                    // The build has just restored the packages: completion and diagnostics now see the
                    // versions and transitive packages it resolved.
                    PostRun(RefreshIntelliSensePackages);
                };
                host.StageChanged += (stage, detail) =>
                {
                    Volatile.Write(ref _runStageValue, (int)stage);
                    PostRun(() => ShowRunStage(stage, detail));
                };
                host.InputRequested += (prompt, isPassword) =>
                    PostRun(() => ShowInputPrompt(prompt, isPassword));

                if (debug && host.Supports(LanguageHostCapabilities.Debug))
                {
                    foreach (var (line, condition, hitCondition) in enabledBreakpoints)
                        host.AddBreakpoint(new ScriptBreakpoint(sourceFilePath, line, condition, hitCondition));
                    host.BreakpointHit += ctx =>
                        PostRun(() => OnBreakpointHit(ctx));
                    host.BreakpointVerified += (bp, verified, message) =>
                        PostRun(() => OnBreakpointVerified(bp, verified, message));
                    await host.RunDebugAsync(
                        script,
                        (data, kind) => PostRun(() => AppendOutput(data, kind)),
                        runCts.Token);
                }
                else
                {
                    runResult = await host.RunAsync(
                        script,
                        (data, kind) => PostRun(() => AppendOutput(data, kind)),
                        runCts.Token);
                }
            }, runCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _runLogger.LogError(ex, "Script host failed");
            if (!_disposed)
            {
                // The stage is named in the message and the status; the exception text is kept as is.
                if (RunStageReached == RunStage.ReadingConnectionSchemas)
                    s.Output.Append("\n<div class=\"error\">Reading the connection schemas failed.</div>");
                AppendError(ex, RunStageReached == RunStage.ReadingConnectionSchemas ? "Connection schemas failed" : "Error");
            }
        }
        finally
        {
            // A debuggee can terminate without going through ResumeDebug/Stop. Invalidate the
            // snapshot and remove any tooltip before the session is torn down.
            _debugHover.Cancel();
            s.Editor.ClearHoverTokenHighlight();
            ToolTip.SetTip(s.Editor.TextArea.TextView, null);
            try
            {
                s.RunningHost = null;
                try { await runCts.CancelAsync(); }
                finally
                {
                    if (!_disposed)
                    {
                        HideInputPrompt();

                        _vm.RunState = RunState.Idle;
                        State.Editor.DebugCurrentLine = null;
                        s.RunStopwatch.Stop();
                        s.ElapsedText = FormatElapsed(s.RunStopwatch.Elapsed);

                        _vm.DebugPanel.ClearStop();
                        if (cancelled)
                        {
                            SetStatus("Cancelled", "RunPad.Status.CancelledBrush");
                        }
                        else if (s.UnhandledDebugException is { } unhandled)
                        {
                            s.HadRunError = true;
                            SetStatus($"Unhandled {DebugPanelViewModel.ShortTypeName(unhandled)}", "RunPad.Status.ErrorBrush");
                        }
                        else if (runResult is { Succeeded: false } failed)
                        {
                            ReportRunFailure(failed, selection is not null);
                        }
                        else if (!s.HadRunError)
                        {
                            SetStatus("Success", "RunPad.Status.SuccessBrush");
                        }

                        UpdateOutputUi();
                        // Also when the script itself failed to compile: the restore before it may have succeeded.
                        RefreshIntelliSensePackages();
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(s.RunCts, runCts)) s.RunCts = null;
                runCts.Dispose();
                _runInProgress = false;
            }
        }
    }

    public void Dispose()
    {
        // Once: the editor services it owns must not be disposed twice.
        if (_disposed) return;
        _disposed = true;
        _prefs.Changed -= OnPreferencesChanged;
        _viewScale.Changed -= OnViewScaleChanged;
        if (_connectionSchemas is not null) _connectionSchemas.Cache.Changed -= OnConnectionSchemaChanged;
        State.Editor.ZoomRequested -= OnEditorZoomRequested;
        _ilHighlighting?.Dispose();
        _ilHighlighting = null;
        _selectedKindSub?.Dispose();
        _filePathSub?.Dispose();
        _selectedSdkSub?.Dispose();
        _vm.DebugPanel.WatchExpressions.CollectionChanged -= OnWatchExpressionsCollectionChanged;
        _contextRefreshCts?.Cancel();
        _debugTooltipCloseCts?.Cancel();
        DisposePreview();
        Cancel();
        // The run owns its CTS until its asynchronous cleanup has finished.
        if (!_runInProgress) State.RunCts?.Dispose();
        _breakpointDialog?.Close();
        _intelliSense?.Dispose(); // owned by this tab — see EditorTabsControl.OnTabsCollectionChanged
        GC.SuppressFinalize(this);
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        var platform = _vm.SelectedSdk ?? _vm.AvailableSdks.FirstOrDefault();
        if (platform is null) return;
        await RunAsync(_vm.DebugModeEnabled, platform);
    }

    // Play/Continue is a single toolbar button — same logic as the Run/Continue hotkey
    // (EditorTabsControl.RunOrContinueActive).
    private void RunOrContinueButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.EnableRunButton) RunButton_Click(sender, e);
        else if (_vm.IsPaused) ResumeDebug(DebugAction.Continue);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Cancel();

    // ── Debug panel handlers ──────────────────────────────────────────────────

    private void DbgStepOver_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepOver);
    private void DbgStepIn_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepIn);
    private void DbgStepOut_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepOut);

    private void DbgPause_Click(object? sender, RoutedEventArgs e) => Pause();

    private void DbgRunToCursor_Click(object? sender, RoutedEventArgs e) => RunToCursor();

    // Also reached from Run → Pause in the main menu, which is enabled whatever the run state.
    public async void Pause()
    {
        if (_vm.RunState != RunState.RunDebug) return;
        try
        {
            if (State.RunningHost is not null)
                await State.RunningHost.PauseAsync(State.RunCts!.Token);
        }
        catch (OperationCanceledException)
        {
            // The run stopped on its own (e.g. debuggee exited) while Pause was in flight — not a
            // real failure, nothing to report.
        }
        catch (Exception ex)
        {
            // async void — an uncaught exception here would crash the whole process.
            // Surface it the same way a failed Run does, instead of losing it entirely.
            AppendError(ex);
        }
    }

    public async void RunToCursor()
    {
        try
        {
            var ctx = State.CurrentDebugCtx;
            if (ctx is null) return;
            var line = State.Editor.TextArea.Caret.Line;
            _debugHover.Cancel();
            State.Editor.ClearHoverTokenHighlight();
            ToolTip.SetTip(State.Editor.TextArea.TextView, null);
            State.CurrentDebugCtx = null;
            _vm.RunState = RunState.RunDebug;
            State.Editor.DebugCurrentLine = null;
            _vm.DebugPanel.ClearStop();
            SetStatus("Process", "RunPad.Status.RunningBrush");
            UpdateOutputUi();
            await ctx.RunToCursorAsync(line);
        }
        catch (OperationCanceledException)
        {
            // The run stopped on its own while Run to Cursor was in flight — not a real failure.
        }
        catch (Exception ex)
        {
            // async void — an uncaught exception here would crash the whole process.
            // Surface it the same way a failed Run does, instead of losing it entirely.
            AppendError(ex);
        }
    }

    private Windows.BreakpointConditionDialog? _breakpointDialog;
    internal Windows.BreakpointConditionDialog? BreakpointDialog => _breakpointDialog;

    private async void OnBreakpointRightClicked(int line, RunPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
    {
        try { await EditBreakpointOptionsAsync(line, bp); }
        catch (Exception ex)
        {
            _runLogger.LogError(ex, "Editing breakpoint options failed");
            if (!_disposed) AppendError(ex);
        }
    }

    private async Task EditBreakpointOptionsAsync(int line, RunPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
    {
        if (_disposed) return;
        _breakpointDialog?.Close();
        var editor = State.Editor;
        var offset = editor.Document.GetLineByNumber(line).Offset;
        using var panel = new BreakpointConditionEditor(bp?.Condition, bp?.HitCondition, bp?.IsEnabled ?? true,
            (expression, ct) =>
            {
                var code = editor.Text;
                return Task.Run(() => _intelliSense?.AnalyzeBreakpointConditionAsync(code, offset, expression, ct)
                    ?? Task.FromResult<BreakpointConditionAnalysis?>(null), ct);
            });
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new Windows.BreakpointConditionDialog(panel, owner);
        _breakpointDialog = dialog;
        try
        {
            var result = await dialog.ShowDialog<(string? Condition, string? HitCondition, bool Enabled)?>(owner);
            if (result is { } value && !_disposed)
                ApplyBreakpointOptions(line, value.Condition, value.HitCondition, value.Enabled);
        }
        finally
        {
            if (ReferenceEquals(_breakpointDialog, dialog)) _breakpointDialog = null;
            dialog.Close();
        }
    }

    internal void ApplyBreakpointOptions(int line, string? condition, string? hitCondition, bool enabled)
    {
        condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        hitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition.Trim();
        var bp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == line);
        if (bp is null)
        {
            // Set all options before publishing the new breakpoint to the running host.
            State.Editor.BreakpointAdded -= OnGutterBreakpointAdded;
            try { State.Editor.AddBreakpoint(line); }
            finally { State.Editor.BreakpointAdded += OnGutterBreakpointAdded; }
            bp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == line);
        }
        if (bp is null) return;
        bp.Condition = condition;
        bp.HitCondition = hitCondition;
        bp.IsEnabled = enabled;
        State.Editor.InvalidateBreakpoints();
        if (State.RunningHost is { } host && host.Supports(LanguageHostCapabilities.Breakpoints)
            && State.SourceFilePath is { } path)
        {
            host.RemoveBreakpoint(new ScriptBreakpoint(path, line, null));
            if (enabled) host.AddBreakpoint(new ScriptBreakpoint(path, line, condition, hitCondition));
        }
    }

    // ── Format / navigation ────────────────────────────────────────────────────

    internal async Task FormatAsync()
    {
        if (_intelliSense is null) return;
        var editor = State.Editor;
        var original = editor.Text;
        var formatted = await _intelliSense.FormatDocumentAsync(original);
        if (formatted is null || formatted == original) return;

        // Formatting can shift line/column boundaries (re-indentation), so the caret is restored
        // on a best-effort basis: same line number (clamped to the new line count), same column
        // (clamped to that line's new length) rather than a precise offset remap.
        var caretLine = editor.TextArea.Caret.Line;
        var caretColumn = editor.TextArea.Caret.Column;

        editor.Document.Text = formatted;

        var line = Math.Min(caretLine, editor.Document.LineCount);
        var column = Math.Min(caretColumn, editor.Document.GetLineByNumber(line).Length + 1);
        editor.TextArea.Caret.Line = line;
        editor.TextArea.Caret.Column = column;
    }

    // ReSharper-style "resolve all missing usings" — ApplyCodeFixAsync's
    // batch-of-one counterpart: resolves every unambiguous unresolved-type diagnostic in the document
    // at once instead of requiring the lightbulb's "using X;" fix to be accepted one at a time. Same
    // application path a single fix's UsingsToAdd already goes through (front matter `namespaces:`
    // for a real .rpad, a prepended `using` line for a plain script with none).
    internal async Task ResolveAllUsingsAsync()
    {
        if (_intelliSense is null) return;
        var code = State.Editor.Text;
        var resolved = await _intelliSense.ResolveAllMissingUsingsAsync(code);
        if (resolved.UsingsToAdd.Count == 0) return;

        // ResolveAllMissingUsingsAsync wraps its work in Task.Run the same way MaterializeFixAsync
        // does — same not-implicit-by-default rationale as ApplyCodeFixAsync's own comment on why it
        // explicitly marshals back too.
        await Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            var editor = State.Editor;
            if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;

            if (State.RpadDocument is not null)
                AddGlobalUsings(resolved.UsingsToAdd);
            else
                editor.Document.Text = PrependUsings(resolved.UsingsToAdd, code);
        });
    }

    internal async void ShowGoToLine()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var editor = State.Editor;
        var maxLine = editor.Document.LineCount;
        var dialog = new Windows.GoToLineDialog(editor.TextArea.Caret.Line, maxLine);
        var result = await dialog.ShowDialog<int?>(owner);
        if (result is not { } line) return;

        editor.TextArea.Caret.Line = Math.Clamp(line, 1, maxLine);
        editor.TextArea.Caret.Column = 1;
        editor.ScrollToLine(editor.TextArea.Caret.Line);
        editor.Focus();
    }

    // F2 (CodeTextEditor.RenameSymbolRequested).
    private async Task ShowRenameSymbolAsync(CodeTextEditor editor, IIntelliSenseService intelliSense)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var caretOffset = editor.TextArea.Caret.Offset;
        var info = await intelliSense.GetRenameInfoAsync(State.Editor.Text, caretOffset, CancellationToken.None);
        if (info is null || info.Spans.Count == 0) return; // nothing renameable at the caret

        var dialog = new Windows.RenameSymbolDialog(info.CurrentName);
        var newName = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrEmpty(newName) || newName == info.CurrentName) return;

        ApplyRenameEdits(editor.Document, info.Spans, newName);
        editor.Focus();
    }

    // Split out from ShowRenameSymbolAsync so the actual edit-application logic (order-of-operations
    // and undo-grouping, both easy to get subtly wrong) can be tested directly against a plain
    // TextDocument, without needing a real modal dialog in the loop.
    internal static void ApplyRenameEdits(TextDocument document, IReadOnlyList<IDocumentHighlightSpan> spans,
        string newName)
    {
        // Descending order so replacing one span never shifts the offsets of spans still to come —
        // Document.Replace is purely offset-based and doesn't adjust sibling calls for you.
        // RunUpdate groups the whole sequence into a single undo step (Ctrl+Z undoes the entire
        // rename, not one occurrence at a time).
        using (document.RunUpdate())
        {
            foreach (var span in spans.OrderByDescending(s => s.Start))
                document.Replace(span.Start, span.Length, newName);
        }
    }

    // Shift+F12 (CodeTextEditor.FindReferencesRequested). Reuses
    // GetDocumentHighlightsAsync (same "every occurrence of the symbol under the caret" data the
    // highlight-occurrences feature already computes) rather than a separate service call — a
    // single-file scratchpad's "references" list and "highlights" list are the same set, they just
    // get shown differently (a clickable menu here vs. inline highlighting there).
    private async Task ShowFindReferencesAsync(CodeTextEditor editor, IIntelliSenseService intelliSense)
    {
        var caretOffset = editor.TextArea.Caret.Offset;
        var highlights = await intelliSense.GetDocumentHighlightsAsync(
            State.Editor.Text, caretOffset, CancellationToken.None);
        if (highlights is null || highlights.Count == 0) return; // nothing to list

        if (BuildFindReferencesMenu(highlights, editor) is not { } menu) return;
        editor.ContextMenu = menu;
        menu.Open(editor);
    }

    // internal (not private): same rationale as EditorTabsControl.BuildAllTabsMenu — lets tests
    // inspect the built menu's items/click-wiring directly, since headless Avalonia has no reliable
    // way to drive an actual popup open/click interaction.
    internal static ContextMenu? BuildFindReferencesMenu(
        IReadOnlyList<IDocumentHighlightSpan> highlights, CodeTextEditor editor)
    {
        if (highlights.Count == 0) return null;

        var menu = new ContextMenu();
        foreach (var span in highlights.OrderBy(h => h.Start))
        {
            var line = editor.Document.GetLineByOffset(span.Start);
            var lineText = editor.Document.GetText(line).Trim();
            var header = new TextBlock { Text = $"Line {line.LineNumber}: {lineText}" };

            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                editor.TextArea.Caret.Offset = span.Start;
                editor.ScrollToLine(editor.TextArea.Caret.Line);
                editor.Focus();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    // "Go to Definition"/"Go to Implementation" (F12) —
    // CodeTextEditor.GoToDefinitionRequested. A single F12 binding covers both: implementations take
    // priority when the symbol has any (same as pressing "Go to Implementation" in VS/Rider, minus a
    // separate Ctrl+F12 — dropped after user feedback 2026-07-19 that modifier+F-key combos collide
    // with desktop-environment global shortcuts often enough on Linux, e.g. KDE Plasma, that adding
    // another one isn't a safe default); falling back to the symbol's own definition otherwise. A
    // symbol declared in the script itself is a same-document caret jump (single-file scratchpad, no
    // cross-file navigation to speak of); a definition outside the script has no source to jump to,
    // so it's decompiled instead and shown in a separate, non-modal window (explicit user request:
    // NOT another tab — the script keeps running/editable behind it, mirroring how dnSpy/ILSpy show a
    // decompiled member without disturbing whatever else is open).
    // ── Coalesced partial redraws for background analysis results ──
    // Semantic colors and inlay hints used to each force a full TextView.Redraw() (every visible line
    // rebuilt and re-colorized, twice per analysis). Now each update reports only the span whose
    // decorations changed; spans arriving before the posted redraw runs are merged, and the redraw
    // covers just their union — nothing at all when the analysis changed nothing.
    private (int Start, int End)? _pendingRedraw;
    private bool _redrawPosted;

    internal int RedrawsRun { get; private set; } // test observability
    internal SemanticHighlighter? SemanticHighlighterForTests => _semanticHighlighter;

    private void RequestRedraw(CodeTextEditor editor, (int Start, int End)? changed)
    {
        if (changed is not { } range) return;
        _pendingRedraw = _pendingRedraw is { } pending
            ? (Math.Min(pending.Start, range.Start), Math.Max(pending.End, range.End))
            : range;
        if (_redrawPosted) return;

        _redrawPosted = true;
        Dispatcher.Post(() =>
        {
            _redrawPosted = false;
            if (_disposed || _pendingRedraw is not { } union || editor.Document is not { } document) return;
            _pendingRedraw = null;
            var start = Math.Clamp(union.Start, 0, document.TextLength);
            var end = Math.Clamp(union.End, start, document.TextLength);
            editor.TextArea.TextView.Redraw(start, Math.Max(end - start, 1)); // a hint is zero-length
            RedrawsRun++;
        }, DispatcherPriority.Background);
    }

    private async Task ShowGoToDefinitionAsync(CodeTextEditor editor, IIntelliSenseService intelliSense)
    {
        var caretOffset = editor.TextArea.Caret.Offset;

        var implementations = await intelliSense.GetImplementationsAsync(
            State.Editor.Text, caretOffset, CancellationToken.None);
        if (implementations is { Count: > 0 })
        {
            if (implementations.Count == 1)
            {
                editor.TextArea.Caret.Offset = implementations[0].Offset;
                editor.ScrollToLine(editor.TextArea.Caret.Line);
                editor.Focus();
            }
            else if (BuildGoToImplementationMenu(implementations, editor) is { } menu)
            {
                editor.ContextMenu = menu;
                menu.Open(editor);
            }

            return;
        }

        var definition = await intelliSense.GetDefinitionAsync(State.Editor.Text, caretOffset, CancellationToken.None);
        if (definition is null) return;

        if (definition.IsInSource)
        {
            editor.TextArea.Caret.Offset = definition.Offset;
            editor.ScrollToLine(editor.TextArea.Caret.Line);
            editor.Focus();
            return;
        }

        DecompiledMemberResult decompiled;
        try
        {
            decompiled = await Task.Run(() => CSharpDecompilerService.Decompile(definition));
        }
        catch (Exception ex)
        {
            decompiled = new DecompiledMemberResult($"// Failed to decompile: {ex.Message}", 1, definition.DisplayName);
        }

        // a new tab of the open explorer, or a new explorer (one per app — WindowService)
        await _windowService.ShowDefinitionInAssemblyExplorerAsync(decompiled, definition);
    }

    // internal (not private): same rationale as BuildFindReferencesMenu — lets tests inspect the
    // built menu's items/click-wiring directly, since headless Avalonia has no reliable way to drive
    // an actual popup open/click interaction.
    internal static ContextMenu? BuildGoToImplementationMenu(
        IReadOnlyList<IImplementationLocation> implementations, CodeTextEditor editor)
    {
        if (implementations.Count == 0) return null;

        var menu = new ContextMenu();
        foreach (var impl in implementations.OrderBy(i => i.Offset))
        {
            var item = new MenuItem { Header = impl.DisplayName };
            item.Click += (_, _) =>
            {
                editor.TextArea.Caret.Offset = impl.Offset;
                editor.ScrollToLine(editor.TextArea.Caret.Line);
                editor.Focus();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    internal void ResumeDebug(DebugAction action)
    {
        _vm.RunState = RunState.RunDebug;
        State.Editor.DebugCurrentLine = null;
        _vm.DebugPanel.ClearStop();
        SetStatus("Process", "RunPad.Status.RunningBrush");
        var ctx = State.CurrentDebugCtx;
        _debugHover.Cancel();
        State.Editor.ClearHoverTokenHighlight();
        ToolTip.SetTip(State.Editor.TextArea.TextView, null);
        State.CurrentDebugCtx = null;
        ctx?.Resume(action);
        UpdateOutputUi();
    }

    private void UpdateOutputUi()
    {
        ExecStatusBlock.Text = State.ExecStatusText;
        ExecStatusBlock.Bind(TextBlock.ForegroundProperty, ExecStatusBlock.GetResourceObservable(
            State.ExecStatusColor == "RunPad.Text.SecondaryBrush"
                ? "RunPad.Text.SecondaryBrush" : "RunPad.StatusBar.ForegroundBrush"));
        if (string.IsNullOrEmpty(State.ExecStatusColor))
            ExecStatusIndicator.Fill = Brushes.Transparent;
        else
            ExecStatusIndicator.Bind(global::Avalonia.Controls.Shapes.Shape.FillProperty,
                ExecStatusIndicator.GetResourceObservable(State.ExecStatusColor));
        ElapsedBlock.Text = State.ElapsedText;
    }

    private void SetStatus(string text, string colorKey)
    {
        State.ExecStatusText = text;
        State.ExecStatusColor = colorKey;
    }


    private void AppendOutput(string data, OutputKind kind)
    {
        if (kind == OutputKind.DumpHtml)
        {
            RenderDump(data);
            return;
        }

        if (kind is OutputKind.StandardError or OutputKind.StandardErrorPartial)
            State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(data)}</div>");
        else
            State.Output.Append($"\n<div class=\"out\">{HttpUtility.HtmlEncode(data)}</div>");
        UpdateOutputUi();
    }

    private static readonly System.Text.RegularExpressions.Regex DumpContainerIdRegex =
        new("data-dp-id=\"([a-f0-9]+)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void RenderDump(string base64)
    {
        try
        {
            var html = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            var wrapped = $"\n<div class=\"dp-dump\">{html}</div>";

            // DumpContainer re-emits the same data-dp-id on every
            // update — replace its existing block in place instead of appending a new one.
            var idMatch = DumpContainerIdRegex.Match(html);
            if (idMatch.Success)
            {
                var id = idMatch.Groups[1].Value;
                if (State.DumpContainerBlocks.TryGetValue(id, out var previous))
                    State.Output.Replace(previous, wrapped);
                else
                    State.Output.Append(wrapped);
                State.DumpContainerBlocks[id] = wrapped;
            }
            else
            {
                State.Output.Append(wrapped);
            }
        }
        catch
        {
            State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(base64)}</div>");
        }

        UpdateOutputUi();
    }

    private static string InvalidDocumentMessage(string parseError)
        => $"Cannot run: this file is not a valid .rpad document. {parseError} " +
           "Fix it in the editor, save, and reopen the file to run it.";

    // The run's status follows the script's real exit code (and a failed compile/start),
    // not "Success" whenever no exception reached the UI. The message is posted like the script's own
    // output, so it lands after the last lines the script wrote.
    private void ReportRunFailure(RunResult result, bool selection = false)
    {
        State.HadRunError = true;
        var (message, status) = result.Outcome switch
        {
            // The compiler's own errors are already in the output; for a selection, say why a name
            // declared elsewhere in the script can be missing.
            RunOutcome.CompilationFailed when selection => (
                "Execute Selection compiles only the selected statements: methods, types and variables declared outside the selection are not available.",
                "Compilation failed"),
            RunOutcome.CompilationFailed => (null, RunStageReached switch
            {
                RunStage.RestoringPackages => "Package restore failed",
                RunStage.GeneratingConnections => "Connection contract failed",
                _ => "Compilation failed",
            }),
            RunOutcome.StartFailed => ("Failed to start the compiled script.", "Error"),
            _ => ($"Process exited with code {result.ExitCode}.", $"Exit code {result.ExitCode}"),
        };
        SetStatus(status, "RunPad.Status.ErrorBrush");
        Dispatcher.Post(() =>
        {
            if (message is not null)
                State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(message)}</div>");
            UpdateOutputUi();
        });
    }

    // Shows the stage a run has reached; the status keeps its running color until the run ends.
    // Only the stage's own progress is shown, never a percentage.
    private void ShowRunStage(RunStage stage, string? detail)
    {
        // A stage reported just before the run ended must not replace its outcome.
        if (_vm.RunState == RunState.Idle) return;
        var text = stage switch
        {
            RunStage.ReadingConnectionSchemas => "Reading connection schemas…",
            RunStage.RestoringPackages => "Restoring packages…",
            RunStage.GeneratingConnections => "Generating connections…",
            RunStage.Compiling => "Compiling…",
            _ => "Process",
        };
        if (detail is not null && stage != RunStage.Running) text = text[..^1] + $" ({detail})…";
        SetStatus(text, "RunPad.Status.RunningBrush");
    }

    private void AppendError(Exception ex, string status = "Error")
    {
        State.HadRunError = true;
        State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>");
        if (ex.StackTrace is { } st)
            State.Output.Append($"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>");
        SetStatus(status, "RunPad.Status.ErrorBrush");
        UpdateOutputUi();
    }

    // Maps the DAP-facing breakpoint (the ScriptBreakpoint passed to host.AddBreakpoint) back to the
    // editor-side gutter breakpoint by line, so BreakPointMargin can color it red/green/yellow.
    private void OnBreakpointVerified(Contracts.IBreakpoint bp, bool verified, string? message)
    {
        var editorBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == bp.Line);
        if (editorBp is null) return;

        editorBp.VerificationState = verified
            ? BreakpointVerificationState.Verified
            : BreakpointVerificationState.Unverified;
        editorBp.VerificationMessage = message;
        State.Editor.InvalidateBreakpoints();
    }

    // Runs on the ILanguageHost.Compiled event, itself raised on the RunAsync's own Task.Run
    // background thread (not the UI thread) — so the disassembly work below happens off-UI already;
    // only the resulting string needs to be marshalled back for display.
    internal void UpdateIlOutput(string binaryPath, int generation)
    {
        string il;
        try
        {
            il = IlDisassembler.Disassemble(binaryPath);
        }
        catch (Exception ex)
        {
            il = $"// Failed to disassemble '{binaryPath}': {ex.Message}";
        }

        AttachIlDocument(il, generation);
    }

    // Safe on any thread: builds the line index here, then the UI thread only adopts the finished document
    // (and highlights it) — and only if no newer run has started meanwhile.
    internal void AttachIlDocument(string il, int generation)
    {
        var document = new AvaloniaEdit.Document.TextDocument(il);
        document.SetOwnerThread(null);
        Dispatcher.Post(() =>
        {
            if (!_disposed && generation == _runGeneration)
            {
                document.SetOwnerThread(Thread.CurrentThread);
                IlOutput.Document = document;
                EnsureIlHighlighting();
            }
        });
    }

    private IlHighlighting? _ilHighlighting;

    // The IL tab is highlighted with the IL grammar in the current editor theme. Installed once, when the
    // tab is in the visual tree (TextMate redraws on install), and kept for every later document: the
    // highlighter follows the editor's document, so the finished document is attached just once.
    private void EnsureIlHighlighting()
    {
        if (_disposed || _ilHighlighting is not null || !IlOutput.IsAttachedToVisualTree()) return;
        _ilHighlighting = IlHighlighting.Install(
            IlOutput, _themeService?.CurrentTheme.Editor.TextMateName ?? ThemeName.DarkPlus);
    }

    internal string? IlGrammarScope => _ilHighlighting?.GrammarScope;
    internal IlHighlighting? IlHighlightingForTests => _ilHighlighting;
    internal int RunGenerationForTests => _runGeneration;

    // ILanguageHost.InputRequested (Util.ReadLine/
    // GetPassword). Always called on the UI thread (see the Dispatcher.Post at the
    // subscription site in RunAsync). internal (not private): lets tests drive the prompt directly,
    // the same rationale as BuildFindReferencesMenu (headless Avalonia has no reliable way to fake a
    // real running script asking for input).
    internal void ShowInputPrompt(string? prompt, bool isPassword)
    {
        if (_disposed) return;
        ++_inputPromptRevision;
        InputPromptLabel.Text = string.IsNullOrEmpty(prompt) ? "Input:" : prompt;
        InputPromptBox.PasswordChar = isPassword ? '●' : default;
        InputPromptBox.Text = "";
        InputPromptBar.IsVisible = true;
        InputPromptBox.Focus();
    }

    internal void HideInputPrompt()
    {
        ++_inputPromptRevision;
        InputPromptBox.Text = "";
        InputPromptBar.IsVisible = false;
    }

    internal bool IsInputPromptVisible => InputPromptBar.IsVisible;
    internal string? InputPromptLabelText => InputPromptLabel.Text;
    internal bool IsInputPromptMasked => InputPromptBox.PasswordChar != default;

    internal string? InputPromptBoxText
    {
        get => InputPromptBox.Text;
        set => InputPromptBox.Text = value;
    }

    private void InputPromptBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SubmitInputPromptAsync();
    }

    internal async Task SubmitInputPromptAsync()
    {
        if (_disposed || !IsInputPromptVisible || State.RunningHost is not { } host) return;

        var text = InputPromptBox.Text ?? "";
        var isPassword = InputPromptBox.PasswordChar != default;
        var generation = _runGeneration;
        var cancellation = State.RunCts?.Token ?? CancellationToken.None;
        HideInputPrompt(); // Consumes this prompt before the first await and clears its text.
        var revision = _inputPromptRevision;
        try
        {
            await host.SendInputAsync(text, cancellation);
            if (IsCurrentSubmission() && !isPassword)
                AppendOutput(text, OutputKind.StandardOutput);
        }
        catch (Exception ex)
        {
            if (!IsCurrentSubmission()) return;
            // A stream exception can contain the submitted value. Report only its type, never its
            // message or exception object, so passwords cannot reach logs or the results panel.
            _runLogger.LogError("Sending script input failed ({ErrorType})", ex.GetType().Name);
            AppendError(new IOException("Could not send input to the script. Stop and run it again."));
            UpdateOutputUi();
        }

        bool IsCurrentSubmission() => !_disposed && !cancellation.IsCancellationRequested
            && generation == _runGeneration && revision == _inputPromptRevision
            && ReferenceEquals(host, State.RunningHost);
    }

    // Forwards gutter breakpoint edits to the running debug session, if there is one — otherwise
    // they only take effect the next time RunAsync reads State.Editor.Breakpoints at run start.
    private void OnGutterBreakpointAdded(object? sender, BreakpointEventArgs e)
    {
        if (State.RunningHost is not { } host || !host.Supports(LanguageHostCapabilities.Breakpoints)) return;
        if (State.SourceFilePath is not { } path) return;
        var editorBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == e.Line);
        if (editorBp?.IsEnabled != false)
            host.AddBreakpoint(new ScriptBreakpoint(path, e.Line, editorBp?.Condition, editorBp?.HitCondition));
    }

    private void OnGutterBreakpointRemoved(object? sender, BreakpointEventArgs e)
    {
        if (State.RunningHost is not { } host || !host.Supports(LanguageHostCapabilities.Breakpoints)) return;
        if (State.SourceFilePath is not { } path) return;
        host.RemoveBreakpoint(new ScriptBreakpoint(path, e.Line, null));
    }

    internal void OnBreakpointHit(IBreakpointHitContext context)
    {
        var s = State;
        _debugHover.Cancel();
        s.Editor.ClearHoverTokenHighlight();
        ToolTip.SetTip(s.Editor.TextArea.TextView, null);
        s.CurrentDebugCtx = context;
        _vm.RunState = RunState.Paused;
        State.Editor.DebugCurrentLine = context.CallStack.Count > 0
            ? context.CallStack[0].Line
            : context.Breakpoint.Line;

        // ToDo: move all of this into DebugPanel
        // Switch to Debugger tab so the user sees variables immediately
        if (_outputTabsControl is { } tc) tc.SelectedIndex = 1;

        _vm.DebugPanel.DebugThreads.Clear();
        foreach (var t in context.Threads)
            _vm.DebugPanel.DebugThreads.Add(new DebugThread(t.Id, t.Name));

        _vm.DebugPanel.DebugLocals.Clear();
        foreach (var v in context.LocalVariables)
            _vm.DebugPanel.DebugLocals.Add(CreateDebugLocal(v, context));

        _vm.DebugPanel.DebugCallStack.Clear();
        foreach (var f in context.CallStack)
            _vm.DebugPanel.DebugCallStack.Add(new DebugFrame(f.MethodName, Path.GetFileName(f.FileName), f.Line));

        // Watch expressions are evaluated against whichever frame/scope is current, so refresh them
        // for the new stop — stale values from the previous breakpoint hit would be misleading.
        foreach (var w in _vm.DebugPanel.WatchExpressions) _ = EvaluateWatchAsync(w);

        _vm.DebugPanel.ShowStop(context);
        if (context.StopReason == DebugStopReason.Exception)
        {
            if (context.Exception is { IsUnhandled: true } unhandled)
                s.UnhandledDebugException = unhandled;
            SetStatus($"Paused on {DebugPanelViewModel.ShortTypeName(context.Exception)}", "RunPad.Status.ErrorBrush");
        }
        else
        {
            SetStatus("Paused", "RunPad.Status.PausedBrush");
        }

        UpdateOutputUi();
    }

    private static DebugLocal CreateDebugLocal(IVariableInfo v, IBreakpointHitContext context)
    {
        Func<Task<IReadOnlyList<DebugLocal>>>? loader = v.HasChildren
            ? async () =>
            {
                var children = await context.GetChildrenAsync(v.VariablesReference);
                return children.Select(c => CreateDebugLocal(c, context)).ToList();
            }
        : null;
        return new DebugLocal(v.Name, v.Value, v.TypeName, v.HasChildren, loader);
    }

    // ── Editor preferences ────────────────────────────────────────────────────

    // The output settings menu's check box: saved like any preference, so every open tab follows it
    // (OnPreferencesChanged); this tab's results reflow at once.
    private void OnResultsWrapCheckBoxChanged(object? sender, RoutedEventArgs e)
    {
        var wrap = ResultsWrapCheckBox.IsChecked == true;
        if (wrap == _prefs.Editor.ResultsWordWrap) return;
        _prefs.Editor.ResultsWordWrap = wrap;
        _prefs.Save();
        ApplyResultsWordWrap(wrap);
    }

    // Only a changed value regenerates the stylesheet, so unrelated settings don't reload the results view.
    private void ApplyResultsWordWrap(bool wrap)
    {
        if (ResultsWrapCheckBox.IsChecked != wrap) ResultsWrapCheckBox.IsChecked = wrap;
        if (wrap == _resultsWordWrap) return;
        _resultsWordWrap = wrap;
        var theme = _themeService.CurrentTheme.Interface;
        State.Output.Css = HtmlThemeStylesheet.Generate(theme.Variant, theme.IsDark, _resultsWordWrap);
    }

    internal bool ResultsWordWrapForTests => _resultsWordWrap;

    private void OnPreferencesChanged()
        => Dispatcher.Post(() =>
        {
            ApplyEditorSettings(State.Editor, _prefs.Editor, _viewScale.CodeZoom);
            ApplyResultsWordWrap(_prefs.Editor.ResultsWordWrap);

            _intelliSense?.UpdateIndentation(_prefs.Editor.UseTabs, _prefs.Editor.IndentationSize);
            _prefs.Keyboard.ApplyTo(State.Editor);
            ApplyPreviewCodeFont();
            if ((_prefs.Experimental?.RunPadAnalyzers == true) != _runPadAnalyzers)
            {
                _runPadAnalyzers = !_runPadAnalyzers;
                _intelliSense?.UpdateRunPadAnalyzers(_runPadAnalyzers);
                _intelliSense?.NotifyTextChanged(State.Editor.Text);
            }

            if (_inlayHintRenderer is { } renderer && renderer.IsEnabled != _prefs.CSharp.InlayHintsEnabled)
            {
                renderer.IsEnabled = _prefs.CSharp.InlayHintsEnabled;
                State.Editor.TextArea.TextView.Redraw();
            }

            // Re-render the lightbulb against the raw candidate lists already on hand — the master
            // switch/exclusion list can change without any new text/caret-move to naturally trigger a
            // re-fetch, so this is the only way a toggle in Preferences reflects immediately instead
            // of waiting for the next keystroke.
            UpdateLightbulb();
        });

    // ── Editor zoom ───────────────────────────────────────────────────────────

    // The editor's own font size: the configured size times the code zoom. The interface scale is
    // not part of it — the window's layout transform already applies that to everything inside.
    internal static double CodeFontSize(EditorSettings s, double codeZoom) => Math.Max(1, s.FontSize * codeZoom);

    private void OnViewScaleChanged()
    {
        State.Editor.FontSize = CodeFontSize(_prefs.Editor, _viewScale.CodeZoom);
        UpdateZoomButton();
    }

    private void UpdateZoomButton() => ZoomButton.Content = $"{_viewScale.CodeZoomPercent}%";

    private void OnEditorZoomRequested(int direction)
    {
        if (direction > 0) _viewScale.ZoomIn();
        else _viewScale.ZoomOut();
    }

    private void SetZoomPercent(int percent)
    {
        _viewScale.SetCodeZoomPercent(percent);
        State.Editor.Focus();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        _viewScale.ZoomIn();
        State.Editor.Focus();
    }

    private void ZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        _viewScale.ZoomOut();
        State.Editor.Focus();
    }

    private void ZoomPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var percent))
            SetZoomPercent(percent);
    }

    // Filters the merged diagnostic-fix + refactoring lists
    // against Preferences → C# → "code fixes enabled"/excluded-diagnostic-id settings at RENDER time,
    // not when each source list is produced: CodeFixesChanged/GetRefactoringsAsync only fire on their
    // own triggers (text change / caret move), so filtering there would leave a stale, unfiltered
    // lightbulb visible until the next one of those happens to fire. Filtering here instead means
    // OnPreferencesChanged can just call this directly to reflect a settings change immediately.
    //
    // Pass 106 (2026-07-21) switched exclusion from a Title substring match to an exact,
    // case-insensitive match against DiagnosticId ("CS0184", "IDE0007", ...) — a stable, documented
    // Roslyn identifier instead of prose a provider could phrase however it likes, per user request.
    // Accepted narrowing versus the earlier title-based filter: a refactoring's DiagnosticId
    // is always null (CodeFixEntry's own doc comment — a CodeRefactoringProvider has no underlying
    // diagnostic to key off), so it can no longer be excluded at all, unlike before when its title
    // could still match a substring. Not worked around (e.g. by keeping a secondary title-based path
    // just for refactorings) — the user's own examples (CS0184, IDE0007) are both diagnostic-driven
    // fixes, and stable IDs are worth the tradeoff of not covering the smaller, less noisy set of
    // pure refactorings too.
    private void UpdateLightbulb()
    {
        var merged = (IEnumerable<CodeFixEntry>)[.. _diagnosticFixEntries, .. _refactoringFixEntries];
        if (!_prefs.CSharp.CodeFixesEnabled)
        {
            State.Editor.UpdateCodeFixes([]);
            return;
        }

        var excluded = _prefs.CSharp.ExcludedDiagnosticIds;
        if (excluded.Count > 0)
            merged = merged.Where(f =>
                f.DiagnosticId is null || !excluded.Contains(f.DiagnosticId, StringComparer.OrdinalIgnoreCase));

        State.Editor.UpdateCodeFixes([.. merged]);
    }

    private static void ApplySelectionColor(CodeTextEditor editor)
    {
        // TextMate may set SelectionBrush to a dark color even for light themes.
        // Read the brush from the current theme so it's fully configurable per theme.
        var variant = Application.Current?.ActualThemeVariant;
        if (Application.Current?.TryGetResource("RunPad.Editor.SelectionBrush", variant, out var res) == true
            && res is IBrush brush)
            editor.TextArea.SelectionBrush = brush;
        editor.TextArea.SelectionForeground = null;
    }

    // Test-only observability (InternalsVisibleTo), same "internal test hook" pattern used elsewhere
    // in the solution — lets a test confirm settings (notably FontFamily) actually reach
    // the real CodeTextEditor without needing to construct a whole EditorTabItemControl.
    internal static void ApplyEditorSettings(CodeTextEditor editor, EditorSettings s, double codeZoom = 1)
    {
        editor.ShowLineNumberText = s.ShowLineNumbers;
        editor.ShowFolding = s.ShowFolding;
        editor.FontSize = CodeFontSize(s, codeZoom);
        editor.FontFamily = MonospaceFont.Resolve(s.FontFamily);
        editor.WordWrap = s.WordWrap;
        editor.Options.ConvertTabsToSpaces = !s.UseTabs;
        editor.Options.IndentationSize = s.IndentationSize;
        if (editor.TextArea.IndentationStrategy is AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy strategy)
            strategy.IndentationString = editor.Options.IndentationString;
        editor.Options.ShowTabs = s.ShowTabs;
        editor.Options.ShowSpaces = s.ShowSpaces;
        editor.Options.ShowEndOfLine = s.ShowEndOfLine;
        editor.Options.ColumnRulerPositions = s.ShowColumnRuler
            ? [s.ColumnRulerPosition]
            : [];
        editor.CommitCharactersEnabled = s.CommitCharactersEnabled;
    }


    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void SetupEditor(CodeTextEditor editor, IIntelliSenseService? intelliSense)
    {
        // Language-specific triggers/brace pairs/indentation are applied later, via
        // ApplyEditorLanguageProfile — see its call site's comment for why not here.
        editor.BreakpointRightClicked += OnBreakpointRightClicked;
        editor.SnippetIcon = CompletionIcons.GetIcon(CompletionItemKind.Snippet);

        if (intelliSense is null) return;

        editor.MemberTemplatePositionProvider = async (code, offset, ct) =>
        {
            try
            {
                return await Task.Run(() => intelliSense.IsEmptyTypeBodyPositionAsync(code, offset, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false; // no offer is the safe answer
            }
        };

        editor.AsyncCompletionProvider = async e =>
        {
            // A genuine Ctrl+Space press (as opposed to the 24/7 auto-invoke/generics/
            // "new "-word triggers, which also set IsExplicitInvoke but pass the real typed character
            // as Trigger, never '\0' — see CodeTextEditor.ShowCompletion's call sites) right in a
            // fresh, not-yet-typed call argument is really asking for parameter info, not a
            // completion list — matching Rider/VS. Gated on the literal hotkey (not the 24/7 paths)
            // so this extra signature-help probe never runs on the hot per-keystroke typing path.
            if (e.IsExplicitInvoke && e.Trigger == '\0')
            {
                var signatureHelp = await intelliSense.GetSignatureHelpAsync(
                    State.Editor.Text, e.CaretOffset, cancellationToken: e.CancellationToken);
                if (signatureHelp is { IsArgumentSlotEmpty: true })
                {
                    e.RedirectToOverloads = true;
                    return;
                }
            }

            var trigger = e.IsExplicitInvoke ? CompletionTrigger.Invoked : CompletionTrigger.Insertion(e.Trigger);
            var list = await intelliSense.GetCompletionsAsync(State.Editor.Text, e.CaretOffset, trigger,
                e.CancellationToken);
            if (list is null)
            {
                // Null here (outside member access, already excluded separately) means the language
                // service found this position not worth completing at all — e.g. inside a comment or
                // string literal (see CSharpIntelliSenseService.IsInsideComment/
                // IsInsideStringOrCharLiteral) — so snippets shouldn't fall back to filling the list
                // either; see CompletionRequestedEventArgs.SuppressSnippets.
                e.SuppressSnippets = true;
                return;
            }

            var isDark = _themeService.CurrentTheme.Editor.IsDark;
            foreach (var item in list.Items)
            {
                // An override-member stub carries a multi-line snippet
                // template instead of a plain name to insert; reuse the exact same
                // SnippetCompletionData/ExpandSnippet machinery the hand-authored Snippets dictionary
                // already uses (reindent-to-current-line + ${0} cursor placement), rather than
                // inventing a second insertion mechanism.
                if (item.InsertTemplate is { } template)
                {
                    e.Items.Add(new SnippetCompletionData(
                        item.DisplayText, template, CompletionIcons.GetIcon(item.Kind),
                        segment => editor.ExpandSnippet(segment.Offset, segment.Length, template)));
                    continue;
                }

                e.Items.Add(new EditorCompletionData(
                    item.DisplayText,
                    () => DisplaySegmentRenderer.BuildWithSummary(item.Signature, item.Summary, isDark),
                    item.Priority,
                    CompletionIcons.GetIcon(item.Kind),
                    // Auto-import: non-empty only for a type from an added
                    // NuGet package that isn't imported yet — accepting it adds the using the same
                    // way the lightbulb already does (AddGlobalUsings), just without that extra step.
                    tags: item.UsingsToAdd,
                    onCommit: AddGlobalUsings,
                    // The list row itself gets a dimmed
                    // "(in Namespace)"/"(+N overloads)" annotation for a not-yet-imported type or a
                    // name collapsing several overloads, and renders bold when the member is declared
                    // directly on the type being completed rather than inherited; all default to
                    // "no annotation, normal weight" for everything else, so BuildListRow just wraps
                    // the plain name (same TextBlock AvaloniaEdit's own default template would've
                    // produced for a bare string Content anyway).
                    content: DisplaySegmentRenderer.BuildListRow(
                        item.DisplayText, item.Detail, item.OverloadCount, item.IsOwnMember)
                ));
            }
        };

        editor.AsyncOverloadProvider = async e =>
        {
            var result = await intelliSense.GetSignatureHelpAsync(
                State.Editor.Text, e.CaretOffset, e.Trigger, e.CancellationToken);
            if (result is null) return;
            var isDark = _themeService.CurrentTheme.Editor.IsDark;
            foreach (var item in result.Items)
            {
                var activeParameter = item.Parameters.ElementAtOrDefault(result.ArgumentIndex);
                e.Overloads.Add((
                    DisplaySegmentRenderer.BuildSignature(item.Signature, isDark),
                    BuildOverloadContentText(item.Summary, activeParameter)));
            }
        };

        editor.AsyncOccurrencesProvider = async e =>
        {
            var highlights = await intelliSense.GetDocumentHighlightsAsync(
                State.Editor.Text, e.CaretOffset, e.CancellationToken);
            if (highlights is null) return;
            foreach (var h in highlights)
                e.Spans.Add((h.Start, h.Length));
        };

        editor.RenameSymbolRequested += async (_, _) => await ShowRenameSymbolAsync(editor, intelliSense);
        editor.FindReferencesRequested += async (_, _) => await ShowFindReferencesAsync(editor, intelliSense);
        editor.GoToDefinitionRequested += async (_, _) => await ShowGoToDefinitionAsync(editor, intelliSense);

        // ── Live diagnostics (squiggles) ──────────────────────────────────────
        var squiggleRenderer = new SquiggleRenderer();
        editor.TextArea.TextView.BackgroundRenderers.Add(squiggleRenderer);

        intelliSense.DiagnosticsChanged += (code, diagnostics) =>
            Dispatcher.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                squiggleRenderer.Update(editor.Document, diagnostics);
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            });

        // ── Semantic highlighting ─────────────────────────────────────────────
        var semanticHighlighter = new SemanticHighlighter();
        semanticHighlighter.SetDark(_themeService.CurrentTheme.Editor.IsDark);
        editor.TextArea.TextView.LineTransformers.Add(semanticHighlighter);
        _semanticHighlighter = semanticHighlighter;

        intelliSense.SemanticTokensChanged += (code, tokens) =>
            Dispatcher.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                RequestRedraw(editor, semanticHighlighter.Update(editor.Document, tokens));
            });

        // ── Inlay hints (inferred `var`/`foreach (var ...)` types) ─────────────
        var inlayHintRenderer = new InlayHintRenderer { IsEnabled = _prefs.CSharp.InlayHintsEnabled };
        editor.TextArea.TextView.ElementGenerators.Add(inlayHintRenderer);
        _inlayHintRenderer = inlayHintRenderer;

        intelliSense.InlayHintsChanged += (code, hints) =>
            Dispatcher.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                RequestRedraw(editor, inlayHintRenderer.Update(editor.Document, hints));
            });

        // The decorations follow ONE document's edits; a new document (file reloaded) starts bare
        // until its first analysis instead of borrowing the old document's offsets.
        editor.DocumentChanged += (_, _) =>
        {
            squiggleRenderer.Update(editor.Document, []);
            semanticHighlighter.Update(editor.Document, []);
            inlayHintRenderer.Update(editor.Document, []);
        };

        // ── Code fixes (lightbulb: real Roslyn CodeFixProviders + CodeRefactoringProviders) ─────
        // Two independent sources feed the same lightbulb margin: diagnostic-driven
        // fixes (CodeFixesChanged, pushed from NotifyTextChanged's background pass whenever the text
        // changes) and caret-position-driven refactorings (GetRefactoringsAsync, pulled below on
        // caret move — a refactoring isn't tied to any diagnostic, so it has no reason to recompute
        // just because the text changed elsewhere in the document). CodeFixMargin itself is unaware
        // of the split — it just renders whatever flat list UpdateLightbulb hands it — so each source
        // keeps its own last-known list here (as instance fields, not locals — OnPreferencesChanged
        // needs to re-filter+re-render the CURRENT lists the instant the user flips a code-fix
        // preference, not only on the next keystroke/caret-move) and remerges on every update from
        // either one.

        // Just Line/Title/
        // Severity/Key now, no NewCode/UsingsToAdd to fold in here: materializing those got deferred
        // to ApplyCodeFixAsync below, run only for the one entry actually clicked, not eagerly for
        // every candidate on every keystroke/caret-move (see ICodeFixInfo's own doc comment for why
        // that used to dominate the whole IntelliSense pipeline's cost).
        static List<CodeFixEntry> ToEntries(IReadOnlyList<ICodeFixInfo> fixes) =>
            fixes.Select(f => new CodeFixEntry(f.Line, f.Title, ToMarginSeverity(f.Severity), f.Key, f.DiagnosticId))
                .ToList();

        intelliSense.CodeFixesChanged += (code, fixes) =>
            Dispatcher.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                _diagnosticFixEntries = ToEntries(fixes);
                UpdateLightbulb();
            });

        editor.FixApplyRequested += fix => _ = ApplyCodeFixAsync(editor, intelliSense, fix);

        // ── Refactorings (lightbulb: real Roslyn CodeRefactoringProviders) ──────
        var refactoringCts = new CancellationTokenSource();

        editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            // Same not-disposed-here rationale as quickInfoCts below — a superseded fetch's
            // background Task.Run can still be mid-flight past this point.
            refactoringCts.Cancel();
            refactoringCts = new CancellationTokenSource();

            // User request 2026-07-21 — the master "show code fixes" switch skips even
            // asking for refactorings, not just hiding them once listed: a caret move fires on every
            // arrow key/click, and Roslyn's own CodeRefactoringProviders do real syntax/semantic work
            // just to decide "am I applicable here", independent of whether the result ever gets
            // filtered out for display.
            if (!_prefs.CSharp.CodeFixesEnabled)
            {
                _refactoringFixEntries = [];
                UpdateLightbulb();
                return;
            }

            var ct = refactoringCts.Token;
            var offset = editor.TextArea.Caret.Offset;
            // a snapshot, materialized only if this caret position survives the debounce — every typed
            // character moves the caret, and editor.Text here copied the whole document per keystroke
            var snapshot = editor.Document.CreateSnapshot();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RefactoringDebounceDelay, ct);
                    var text = snapshot.Text;
                    var refactorings = await intelliSense.GetRefactoringsAsync(text, offset, ct);
                    if (ct.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed || ct.IsCancellationRequested) return;
                        if (!ReferenceEquals(editor.Text, text) && editor.Text != text) return;
                        _refactoringFixEntries = refactorings is null ? [] : ToEntries(refactorings);
                        UpdateLightbulb();
                    });
                }
                catch (OperationCanceledException)
                {
                }
            });
        };

        // An immutable snapshot (cheap: the rope is shared) instead of editor.Text — the string is built
        // off the UI thread, and only for the keystroke that's still the latest once analysis starts.
        editor.TextChanged += (_, _) =>
        {
            var snapshot = editor.Document.CreateSnapshot();
            intelliSense.NotifyTextChanged(() => snapshot.Text);
        };

        // ── Quick Info tooltips on hover ──────────────────────────────────────
        ToolTip.SetShowDelay(editor.TextArea.TextView, 0);
        var quickInfoCts = new CancellationTokenSource();

        editor.TextArea.TextView.PointerMoved += (_, e) =>
        {
            // Not disposed here — a superseded hover's background Task.Run may still be mid-flight
            // past this point, and registering/reading against an already-disposed
            // CancellationTokenSource throws ObjectDisposedException. A
            // CancellationTokenSource whose WaitHandle is never touched holds no unmanaged
            // resources, so leaving the old one for the GC is safe.
            quickInfoCts.Cancel();
            quickInfoCts = new CancellationTokenSource();
            _debugTooltipCloseCts?.Cancel();
            _debugTooltipInside = false;
            var ct = quickInfoCts.Token;
            _debugTooltipCloseCts?.Cancel();
            var (debugGeneration, debugToken) = _debugHover.Begin();
            var pos = e.GetPosition(editor);

            _ = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, debugToken);
                var requestCt = linked.Token;
                try
                {
                    await Task.Delay(QuickInfoHoverDelay, requestCt);
                    var (offset, text, identifierChars, debugContext, debugLocals) =
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var context = State.CurrentDebugCtx;
                            return (editor.GetOffsetFromPoint(pos), editor.Text,
                                editor.IdentifierChars, context, DebugHoverController.Capture(context));
                        });
                    if (offset < 0 || ct.IsCancellationRequested) return;

                    var symbol = DebugHoverSymbolExtractor.Extract(text, offset, identifierChars);
                    var debugValue = DebugHoverController.ResolveSnapshot(debugLocals, symbol);
                    // Snapshot locals are preferred. Only the bounded, identifier/dotted-token policy
                    // may fall back to DAP evaluation; never send arbitrary editor text.
                    if (debugValue is null && debugContext is not null &&
                        ReferenceEquals(debugContext, State.CurrentDebugCtx) && symbol is not null)
                        debugValue = await _debugHover.EvaluateHoverAsync(debugContext, symbol, requestCt);
                    if (debugValue is not null)
                    {
                        if (symbol is null) return;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_debugHover.IsCurrent(debugGeneration) && !ct.IsCancellationRequested &&
                                ReferenceEquals(debugContext, State.CurrentDebugCtx))
                            {
                                var start = offset;
                                while (start > 0 && identifierChars.Contains(text[start - 1])) start--;
                                editor.SetHoverTokenHighlight(start, symbol.Length,
                                    ThemeBrush("RunPad.Editor.HoverHighlightBrush", new SolidColorBrush(Color.FromArgb(55, 90, 140, 210))));
                                ToolTip.SetTip(editor.TextArea.TextView,
                                    BuildDebugHoverContent(debugValue!, debugContext, debugGeneration, debugToken,
                                        symbol));
                            }
                        });
                        return;
                    }

                    // A diagnostic under the cursor takes priority over Quick Info — seeing why a
                    // squiggle is there is usually more useful than the symbol's signature.
                    var diagnostic =
                        squiggleRenderer.Diagnostics.FirstOrDefault(d =>
                            offset >= d.StartOffset && offset < d.EndOffset);
                    var quickInfo = diagnostic is null
                        ? await intelliSense.GetQuickInfoAsync(text, offset, requestCt)
                        : null;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !_debugHover.IsCurrent(debugGeneration)) return;

                        // Built here, not above — DisplaySegmentRenderer creates Avalonia controls,
                        // which must happen on the UI thread, unlike the rest of this background task.
                        object? content = diagnostic is not null
                            ? diagnostic.Message
                            : quickInfo is null
                                ? null
                                : DisplaySegmentRenderer.BuildWithSummary(
                                    quickInfo.Signature, quickInfo.Summary, _themeService.CurrentTheme.Editor.IsDark);
                        ToolTip.SetTip(editor.TextArea.TextView, content);
                    });
                }
                catch (OperationCanceledException)
                {
                }
            });
        };

        editor.TextArea.TextView.PointerExited += (_, _) =>
        {
            quickInfoCts.Cancel();
            ScheduleDebugTooltipClose(editor);
        };
    }

    private void ScheduleDebugTooltipClose(CodeTextEditor editor)
    {
        _debugTooltipCloseCts?.Cancel();
        var cts = _debugTooltipCloseCts = new CancellationTokenSource();
        _ = CloseAfterGraceAsync(editor, cts.Token);
    }

    private async Task CloseAfterGraceAsync(CodeTextEditor editor, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            if (token.IsCancellationRequested || _debugTooltipInside) return;
            _debugHover.Cancel();
            editor.ClearHoverTokenHighlight();
            ToolTip.SetTip(editor.TextArea.TextView, null);
        }
        catch (OperationCanceledException) { }
    }

    private Control BuildDebugHoverContent(IVariableInfo root, IBreakpointHitContext? context,
        long generation, CancellationToken token, string symbol)
    {
        var panel = new StackPanel { Spacing = 2, MaxWidth = 520, MaxHeight = 360 };
        AddDebugVariableRow(panel, root, 0, context, generation, token, new DebugHoverController.TreeBudget(), symbol);
        var border = new Border
        {
            Padding = new Thickness(8, 5),
            Background = ThemeBrush("RunPad.Panel.BackgroundBrush", Brushes.Transparent),
            Child = panel,
        };
        border.PointerEntered += (_, _) =>
        {
            _debugTooltipInside = true;
            _debugTooltipCloseCts?.Cancel();
        };
        border.PointerExited += (_, _) =>
        {
            _debugTooltipInside = false;
            ScheduleDebugTooltipClose(State.Editor);
        };
        return border;
    }

    private void AddDebugVariableRow(Panel panel, IVariableInfo variable, int depth,
        IBreakpointHitContext? context, long generation, CancellationToken token,
        DebugHoverController.TreeBudget budget, string rootSymbol)
    {
        if (!budget.TryReserve()) return;
        var row = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(depth * 14, 0, 0, 0),
            Background = depth == 0 ? ThemeBrush("RunPad.Editor.HoverHighlightBrush", new SolidColorBrush(Color.FromArgb(35, 90, 140, 210))) : null
        };
        if (variable.HasChildren && context is not null && depth < DebugHoverController.MaxDepth)
        {
            var expand = new Button { Content = ">", Padding = new Thickness(2, 0), MinWidth = 18 };
            expand.Click += async (_, _) =>
            {
                expand.IsEnabled = false;
                try
                {
                    if (token.IsCancellationRequested || !_debugHover.IsCurrent(generation) || !ReferenceEquals(context, State.CurrentDebugCtx)) return;
                    var children = await _debugHover.LoadChildrenAsync(context, variable, depth, token);
                    if (!_debugHover.IsCurrent(generation) || !ReferenceEquals(context, State.CurrentDebugCtx)) return;
                    foreach (var child in children)
                        AddDebugVariableRow(panel, child, depth + 1, context, generation, token, budget, rootSymbol);
                    expand.Content = "v";
                }
                catch (OperationCanceledException) { }
                finally { expand.IsEnabled = true; }
            };
            row.Children.Add(expand);
        }
        row.Children.Add(new TextBlock { Text = $"{variable.Name} =", Foreground = ThemeBrush("RunPad.Text.PrimaryBrush", Brushes.Black) });
        row.Children.Add(new TextBlock { Text = $"{{{variable.TypeName}}}", Foreground = ThemeBrush("RunPad.Text.SecondaryBrush", Brushes.Gray) });
        row.Children.Add(new TextBlock { Text = variable.Value, Foreground = ThemeBrush("RunPad.Dump.StringBrush", Brushes.DarkGreen) });
        panel.Children.Add(row);
    }

    private static IBrush ThemeBrush(string key, IBrush fallback)
    {
        var app = Application.Current;
        return app?.TryGetResource(key, app.ActualThemeVariant, out var value) == true
            && value is IBrush brush ? brush : fallback;
    }

    // Applies a language's lexical/syntactic conventions (ILanguageHostFactory.EditorProfile) to the
    // language-agnostic CodeTextEditor — triggers/identifier chars/brace pairs/indentation used to be
    // a single hardcoded C#-shaped profile applied to every language including VB; this
    // is the same pattern already used for Snippets, just for the rest of the editor's language-aware
    // configuration.
    private static void ApplyEditorLanguageProfile(CodeTextEditor editor, EditorLanguageProfile profile)
    {
        foreach (var ch in profile.CompletionTriggers) editor.CompletionTriggers.Add(ch);
        foreach (var ch in profile.OverloadTriggers) editor.OverloadTriggers.Add(ch);
        foreach (var ch in profile.IdentifierChars) editor.IdentifierChars.Add(ch);
        foreach (var ch in profile.ContextualTriggers) editor.ContextualTriggers.Add(ch);
        // Assigned wholesale, not Add()-ed into the default set, so a case-insensitive language
        // (VB, see VbLanguageHostFactory.EditorProfile) keeps its comparer — copying entries one at
        // a time into a fresh default-comparer HashSet would silently drop it.
        editor.WordTriggers = profile.WordTriggers;
        foreach (var ch in profile.StatementTerminators) editor.StatementTerminators.Add(ch);
        foreach (var (open, close) in profile.BracePairs) editor.BracePairs[open] = close;
        foreach (var ch in profile.AutoCloseBraces) editor.AutoCloseBraces.Add(ch);
        editor.LineCommentPrefix = profile.LineCommentPrefix;
        editor.MemberTemplateSnippet = profile.MemberTemplateSnippet;
        editor.MemberTemplateTitle = profile.MemberTemplateTitle;

        editor.TextArea.IndentationStrategy = profile.IndentationStrategy switch
        {
            IndentationStrategyKind.CStyle =>
                new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(editor.Options),
            _ => new AvaloniaEdit.Indentation.DefaultIndentationStrategy(),
        };

        // No more switch on a FoldingStrategyKind enum naming a specific concrete type
        // (e.g. VbFoldingStrategy) — the language plugin hands over its own algorithm as a plain
        // delegate (or nothing, for the brace-based default), so this main-project file no longer
        // needs to know which languages exist or what their folding rules look like.
        editor.FoldingStrategy = profile.FoldingRegionsProvider is { } computeFoldRegions
            ? new DelegateFoldingStrategy(computeFoldRegions)
            : new BraceFoldingStrategy();
    }

    // The signature-help header already shows the full (syntax-highlighted) signature with the
    // active parameter bolded, so the content area below it only needs the descriptive text: the
    // method's own summary plus the active parameter's own <param> doc, LINQPad/VS Code-style.
    private static string? BuildOverloadContentText(string? methodSummary, ISignatureHelpParameter? activeParameter)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(methodSummary)) parts.Add(methodSummary);
        if (!string.IsNullOrEmpty(activeParameter?.Summary))
            parts.Add($"{activeParameter.Name}: {activeParameter.Summary}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    internal RecoveryTab CaptureRecovery() => new()
    {
        Id = _vm.RecoveryId,
        FilePath = _vm.FilePath,
        Title = _vm.FileName,
        EditorText = State.Editor.Text,
        Document = State.RpadDocument?.CaptureSnapshot(),
        ParseError = State.DocumentParseError,
        IsDirty = _vm.IsDirty || FrontMatterSnapshot() != _metadataBaseline,
        BaselineKnown = !_vm.RequiresBaselineConfirmation && _vm.DiskState is not null,
        DiskHash = _vm.DiskState?.Hash?.ToArray(),
        MetadataBaseline = _metadataBaseline,
        CaretOffset = State.Editor.CaretOffset,
        SelectionStart = State.Editor.SelectionStart,
        SelectionLength = State.Editor.SelectionLength,
        // Provenance of an imported draft stays with it until it is saved to a file of its own.
        OriginalPath = _vm.FilePath is null ? _vm.RestoredRecovery?.OriginalPath : null,
        ImportedFrom = _vm.RestoredRecovery?.ImportedFrom,
    };

    private void SetDocumentContent(CodeTextEditor editor, string? filePath)
    {
        string content;
        RpadDocument? rpad = null;

        if (_vm.RestoredRecovery is { } recovered)
        {
            rpad = recovered.Document is null ? null : RpadDocument.FromSnapshot(recovered.Document);
            content = recovered.EditorText;
            State.DocumentParseError = recovered.ParseError;
            _vm.DiskState = recovered.BaselineKnown ? new ScriptFileState(recovered.DiskHash?.ToArray()) : null;
            _vm.RequiresBaselineConfirmation = !recovered.BaselineKnown;
            if (filePath is not null)
                State.OutputDirectory = _buildCache.EnsureOutputDirectory(filePath, EnsureWorkDirectory());
        }
        else if (filePath is null && _vm.Example is { } example)
        {
            // An example: an untitled, editable copy of its whole document — the code, its front
            // matter and the Markdown around it, which Save As writes out — never the original.
            var parsed = RpadParser.Parse(example.Text);
            rpad = parsed.Document;
            content = rpad?.RunCode ?? "";
        }
        else if (filePath is null)
        {
            // Deferred creation ("tab lifecycle") — a brand-new tab is genuinely
            // empty, not the old "Hello, RPad!" sample, and stays in-memory only until the user does
            // Save/Save As. It still gets a real (in-memory) RpadDocument built from the language/kind
            // chosen in NewFileKindDialog (_vm.NewFileLanguage/NewFileKind) rather than staying null —
            // that's what lets the toolbar's kind combobox, IntelliSense's kind-aware analysis, and
            // Run/Debug all already behave correctly for the chosen language/kind before the first
            // save, the same as they do for a file freshly loaded from disk.
            // Empty, except that a Program starts with its Main skeleton.
            content = _registry.NewScriptCode(_vm.NewFileLanguage, _vm.NewFileKind);
            rpad = RpadParser.TryParse(RpadWriter.NewFile(
                "", title: "Untitled", language: _vm.NewFileLanguage, kind: _vm.NewFileKind));
        }
        else if (File.Exists(filePath))
        {
            (content, rpad) = ReadScript(File.ReadAllBytes(filePath));

            // Allocate/reuse this file's persistent build directory now, at open time, rather than
            // lazily on first Run — see EnsureWorkDirectory/BuildRunOptions for how it's consumed.
            State.OutputDirectory = _buildCache.EnsureOutputDirectory(filePath, EnsureWorkDirectory());
        }
        else
        {
            content = "";
            // A file that appears later was made by someone else: saving must not replace it unasked.
            if (filePath is not null) _vm.DiskState = ScriptFileState.Absent;
        }

        ApplyDocument(rpad);

        if (_vm.RestoredRecovery is { } snapshot)
        {
            // A recovery payload is exact editor text, never a template: even ${0} is literal.
            editor.Document = new TextDocument(content);
            _metadataBaseline = snapshot.MetadataBaseline ?? FrontMatterSnapshot();
            _vm.IsDirty = snapshot.IsDirty;
            editor.Select(Math.Clamp(snapshot.SelectionStart, 0, content.Length),
                Math.Clamp(snapshot.SelectionLength, 0, content.Length - Math.Clamp(snapshot.SelectionStart, 0, content.Length)));
            editor.CaretOffset = Math.Clamp(snapshot.CaretOffset, 0, content.Length);
        }
        else if (filePath is not null)
        {
            editor.Document = new TextDocument(content);
        }
        else if (_vm.Example is not null)
        {
            // verbatim (no template caret marker), and unsaved: closing asks, Save goes to Save As
            editor.Document = new TextDocument(content);
            _vm.IsDirty = true;
        }
        else
        {
            var (text, caret) = CodeTemplate.TakeCaret(content);
            editor.Document = new TextDocument(text);
            if (caret > 0) editor.CaretOffset = caret;
        }

        // Shows which kind/target framework the script actually runs as —
        // helps tell the two compile paths apart and diagnose compile/run discrepancies without
        // having to open the front matter by hand. Was left commented out with the XAML control
        // itself missing — both restored.
        UpdateProjectInfoBlock();
    }

    // The script in a file's exact bytes, which become what later saves compare the file with. An
    // invalid document comes back as the whole file and no RpadDocument, and says why in the output.
    private (string Content, RpadDocument? Document) ReadScript(byte[] bytes)
    {
        _vm.DiskState = ScriptFileState.Of(bytes);
        _vm.RequiresBaselineConfirmation = false;
        var raw = DecodeScript(bytes);
        var parsed = RpadParser.Parse(raw);
        // Invalid document: show the whole file as-is instead of default metadata —
        // WriteFile's no-RpadDocument branch then saves it back verbatim, and RunAsync refuses it.
        State.DocumentParseError = parsed.Error;
        if (parsed.Error is { } error)
        {
            State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(InvalidDocumentMessage(error))}</div>");
            SetStatus("Invalid document", "RunPad.Status.ErrorBrush");
            UpdateOutputUi();
        }
        return (parsed.Document?.RunCode ?? raw, parsed.Document);
    }

    // Decoded as File.ReadAllText does.
    private static string DecodeScript(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Makes rpad the tab's document: the toolbar, IntelliSense and the baseline that tells a
    // front-matter edit apart follow its header. The editor's text is the caller's to set.
    private void ApplyDocument(RpadDocument? rpad)
    {
        State.RpadDocument = rpad;
        _metadataBaseline = FrontMatterSnapshot();
        _vm.FrontMatter = rpad?.FrontMatter;
        _vm.SelectedKind = rpad?.FrontMatter.Kind.ToString() ?? "Statements";
        // The toolbar shows the SDK the script's targetSdk names: "latest (…)" for `latest`, else that
        // version (or the newest, when it isn't installed — RunAsync then says so).
        var targetSdk = rpad?.FrontMatter.TargetSdk ?? TargetSdk.Default;
        _syncingSdk = true;
        try
        {
            _vm.SelectedSdk = TargetSdkResolver.IsLatest(targetSdk)
                ? _vm.LatestSdk
                : TargetSdkResolver.Resolve(_vm.AvailableSdks, targetSdk);
        }
        finally
        {
            _syncingSdk = false;
        }

        // Push kind/packages/usings into IntelliSense BEFORE the caller hands the editor its text:
        // setting editor.Document fires TextChanged synchronously, which is what actually triggers the
        // first NotifyTextChanged/diagnostics pass. Doing this the other way around (as it used to
        // be) meant that very first pass always ran against IntelliSense's stale defaults — kind
        // still RpadKind.Statements, no packages, no usings — since UpdateKind/UpdatePackageReferences/
        // UpdateGlobalUsings only invalidate the cached analysis document, they don't themselves
        // trigger a fresh pass. For a Program-kind file, that meant a spurious "declared but never
        // used" CS8321 on the user's own Main sitting in the diagnostics panel until their next
        // keystroke; for a file with front-matter packages/usings, spurious "type not found"
        // squiggles for anything only resolvable through them.
        RefreshIntelliSenseKind();
        // Applied off the UI thread now, so the text is analysed again once the context is in rather
        // than trusting the first pass to come after it.
        RefreshIntelliSenseContext(reanalyze: true);
        if (rpad?.FrontMatter.Namespaces is { Count: > 0 })
            RefreshIntelliSenseUsings();
    }

    /// <summary>
    /// Replaces the tab's script with what its file holds now — the code as one undoable edit, so
    /// Undo brings back the text it replaced — and leaves the tab saved. The caller decides whether
    /// unsaved edits may go.
    /// </summary>
    internal DiskReload ReloadFromDisk()
    {
        if (_disposed || _vm.FilePath is not { } path) return DiskReload.Unavailable;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskReload.Unavailable;
        }

        // This tab's editor services, grammar and snippets are its language's; a script that is now
        // in another language needs a tab of its own.
        var language = State.RpadDocument?.FrontMatter.Language ?? RpadLanguage.CSharp;
        if (RpadParser.TryParse(DecodeScript(bytes))?.FrontMatter.Language is { } newLanguage && newLanguage != language)
            return DiskReload.OtherLanguage;

        var (content, rpad) = ReadScript(bytes);
        _vm.DeclinedDiskState = null;
        ApplyDocument(rpad);
        // Usings that are gone from the header must leave IntelliSense too.
        RefreshIntelliSenseUsings();

        var editor = State.Editor;
        var text = content;
        if (editor.Document.Text != text)
        {
            var caret = editor.CaretOffset;
            editor.Document.Replace(0, editor.Document.TextLength, text);
            editor.CaretOffset = Math.Min(caret, editor.Document.TextLength);
        }
        else
        {
            // Only the header changed; the text edit that would analyse the code again never came.
            _intelliSense?.NotifyTextChanged(text);
        }
        _vm.IsDirty = false;
        UpdateProjectInfoBlock();
        UpdatePreviewNow();
        return DiskReload.Reloaded;
    }

    /// <summary>
    /// Pushes this tab's current NuGet packages into its own IntelliSense instance, with where this
    /// tab builds, so it can use the assemblies the last restore resolved. Called on initial load,
    /// after the Dependencies window closes (it mutates the same RpadFrontMatter in place without any
    /// change notification of its own), after each successful build and when the SDK changes.
    /// </summary>
    internal void RefreshIntelliSensePackages() => RefreshIntelliSenseContext(reanalyze: false);

    // The latest refresh of the IntelliSense context; an older one still running is cancelled.
    private CancellationTokenSource? _contextRefreshCts;

    // Only the request is put together on the UI thread, from copies of the state it reads: the
    // connections' code (the schemas come from disk), the contract assembly lookup and the reference
    // resolution take as long as the script's schemas are large, so they run on a worker and the
    // editor keeps typing meanwhile. A refresh that is overtaken by a newer one is dropped, never
    // applied over it. The text is analysed again, once, only when asked and only for the newest.
    private void RefreshIntelliSenseContext(bool reanalyze)
    {
        if (_disposed || _intelliSense is not { } intelliSense) return;
        _contextRefreshCts?.Cancel();
        var refresh = _contextRefreshCts = new CancellationTokenSource();
        var token = refresh.Token;
        var frontMatter = State.RpadDocument?.FrontMatter;
        // The same request a run makes, so the editor reads the references and packages as the build does.
        // The lists are copied: the Dependencies window edits the front matter in place.
        var baseRequest = ExecutionRequest.For(frontMatter, _vm.FilePath) with
        {
            ReferenceBasePath = _vm.ReferenceAnchorPath,
            Platform = _vm.SelectedSdk ?? _vm.AvailableSdks.FirstOrDefault(),
            OutputDirectory = State.OutputDirectory,
            RuntimePath = _prefs.Debugger?.DotnetPath,
        };
        baseRequest = baseRequest with { Usings = [.. baseRequest.Usings], References = [.. baseRequest.References] };
        List<RpadConnection> connections = [.. frontMatter?.Connections ?? []];
        var language = frontMatter?.Language ?? RpadLanguage.CSharp;
        List<string> packageIds = frontMatter is null ? [] : [.. frontMatter.Packages.Keys];
        var scriptDirectory = ScriptDirectory;
        _ = Task.Run(() =>
        {
            try
            {
                var generated = frontMatter is null ? null
                    : GeneratedConnections.For(connections, language, packageIds, _connectionDrivers, _connectionSchemas?.Cache, scriptDirectory);
                token.ThrowIfCancellationRequested();
                intelliSense.ApplyExecutionContext(baseRequest with { Connections = generated }, token);
                if (reanalyze)
                    Dispatcher.Post(() =>
                    {
                        // The classes the script's code refers to are new: analyse it again now rather than at the next keystroke.
                        if (!_disposed && !token.IsCancellationRequested) _intelliSense?.NotifyTextChanged(State.Editor.Text);
                    });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _runLogger.LogWarning(ex, "Updating the editor's references failed");
            }
        }, token);
        EnsureConnectionSchemasInBackground();
    }

    // A schema read elsewhere (Dependencies' Refresh, the Connections window, a run in another tab)
    // changes the classes of this script's connection with the same key: catch up, as after an edit.
    private void OnConnectionSchemaChanged(string key) => Dispatcher.Post(() =>
    {
        if (_disposed || _connectionSchemas is not { } schemas
            || State.RpadDocument?.FrontMatter.Connections is not { Count: > 0 } connections) return;
        var scriptDirectory = ScriptDirectory;
        if (!connections.Any(c => schemas.Cache.KeyOf(c, scriptDirectory) == key)) return;
        RefreshIntelliSenseContext(reanalyze: true);
    });

    // Reads the schemas of connections that have never been read (a gRPC server's services); the
    // cache's Changed event then lets the editor know their classes. One that fails isn't asked again
    // here: its error is what the editor shows, and a run, Refresh or the Connections window tries again.
    private void EnsureConnectionSchemasInBackground()
    {
        if (_connectionSchemas is not { } schemas || State.RpadDocument?.FrontMatter.Connections is not { Count: > 0 } connections) return;
        List<RpadConnection> list = [.. connections];
        var scriptDirectory = ScriptDirectory;
        _ = Task.Run(async () =>
        {
            try
            {
                var before = list.Select(connection => schemas.Cache.TryGetLoaded(connection, scriptDirectory)).ToArray();
                var changed = await schemas.EnsureAsync(list, scriptDirectory, ConnectionSchemaRefresh.Missing, CancellationToken.None).ConfigureAwait(false);
                var loadedOrUpdated = list.Select((connection, index) =>
                    !ReferenceEquals(before[index], schemas.Cache.TryGetLoaded(connection, scriptDirectory))).Any(x => x);
                if (changed || loadedOrUpdated)
                    Dispatcher.Post(() =>
                    {
                        if (_disposed) return;
                        RefreshIntelliSensePackages();
                        // The new classes (or error) change what the code already in the editor
                        // means: analyse it again now rather than at the next keystroke.
                        _intelliSense?.NotifyTextChanged(State.Editor.Text);
                    });
            }
            catch (Exception ex)
            {
                _runLogger.LogWarning(ex, "Reading connection schemas failed");
            }
        });
    }

    // A front-matter edit (kind, usings, packages, references) is a real unsaved
    // change just like typed text. It used to be written straight away without ever setting
    // IsDirty — so if that write failed, the tab still looked clean and the next close/quit
    // dropped the edit silently. Flag dirty FIRST, then write: a successful write clears it again,
    // a failed one leaves the tab dirty for the close/quit decision. For an untitled tab
    // SaveIfNeeded is a no-op, so it simply stays dirty until Save As.
    private void CommitMetadataChange()
    {
        _metadataBaseline = FrontMatterSnapshot();
        _vm.IsDirty = true;
        UpdatePreviewNow();
        SaveIfNeeded();
    }

    // Dependencies window closed — on OK it copies its draft into the front matter with no change
    // notification, so compare against the last accounted-for snapshot to tell a real edit apart
    // from Cancel or OK without changes. Those must not make an untitled tab dirty, and must not
    // save unrelated unsaved editor text of a dirty named tab either.
    internal void OnDependenciesEdited()
    {
        if (FrontMatterSnapshot() == _metadataBaseline) return;

        RefreshIntelliSensePackages();
        RefreshIntelliSenseUsings();
        CommitMetadataChange();
    }

    private string FrontMatterSnapshot()
    {
        if (State.RpadDocument?.FrontMatter is not { } fm) return "";
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            fm.Version,
            fm.Language,
            fm.TargetSdk,
            fm.Kind,
            Packages = fm.Packages.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            fm.References,
            fm.Namespaces,
            fm.Args,
            fm.AllowUnsafe,
            fm.Connections,
            fm.UnknownEntries,
        });
    }

    /// <summary>Pushes this tab's front-matter `namespaces:` list into its own IntelliSense instance.</summary>
    private void RefreshIntelliSenseUsings()
        => _intelliSense?.UpdateGlobalUsings(State.RpadDocument?.FrontMatter.Namespaces ?? []);

    // Pushes this tab's front-matter `kind:` into its own IntelliSense instance, unconditionally
    // (unlike Packages/Namespaces above, Kind always has a meaningful value — there's no "empty" case
    // to skip) — so live analysis mirrors the same kind-specific transform the compiler applies
    //.
    private void RefreshIntelliSenseKind()
        => _intelliSense?.UpdateKind(State.RpadDocument?.FrontMatter.Kind ?? RpadKind.Statements);

    // Adds namespaces to the .rpad front matter's `namespaces:` list (compiled in as `global using` —
    // see docs/ImplementationPlanRPad.md) instead of inserting `using X;` text into the script, and
    // persists + re-syncs IntelliSense immediately so the fix's effect (squiggle gone) is visible
    // right away rather than waiting for the next save/run.
    private void AddGlobalUsings(IReadOnlyList<string> usings)
    {
        var rpad = State.RpadDocument;
        if (rpad is null) return;

        var fmUsings = rpad.FrontMatter.Namespaces ??= [];
        var added = false;
        foreach (var ns in usings)
            if (!fmUsings.Contains(ns))
            {
                fmUsings.Add(ns);
                added = true;
            }

        if (!added) return;

        RefreshIntelliSenseUsings();
        _intelliSense?.NotifyTextChanged(State.Editor.Text);
        CommitMetadataChange();
    }

    // FrontMatter.Kind is the source of truth (like usings/packages/references above) — SelectedKind
    // is just the combobox's mirror of it. Guards against writing on the initial sync in
    // SetDocumentContent (which sets SelectedKind from the same value already on FrontMatter.Kind)
    // and only reacts to genuine user-driven changes.
    private void OnSelectedKindChanged(string? kind)
    {
        var rpad = State.RpadDocument;
        if (rpad is null || kind is null) return;
        if (!Enum.TryParse(kind, out RpadKind parsedKind) || rpad.FrontMatter.Kind == parsedKind) return;

        rpad.FrontMatter.Kind = parsedKind;
        ApplyKindTemplate(parsedKind);
        RefreshIntelliSenseKind();
        UpdateProjectInfoBlock();
        _intelliSense?.NotifyTextChanged(State.Editor.Text);
        CommitMetadataChange();
    }

    // Switching to Program gives an empty script its Main skeleton and wraps existing statements in
    // Main (the language decides how; see ILanguageHostFactory.CodeForKind) — one replacement, so a
    // single Undo brings the old code back. Code that already fits the kind is left alone, and so is
    // every switch away from Program.
    private void ApplyKindTemplate(RpadKind kind)
    {
        if (_registry.GetCodeForKind(ResolveLanguageId(), State.Editor.Text, kind) is not { } code) return;
        var (text, caret) = CodeTemplate.TakeCaret(code);
        var document = State.Editor.Document;
        document.Replace(0, document.TextLength, text);
        State.Editor.CaretOffset = Math.Max(0, caret);
        State.Editor.TextArea.Caret.BringCaretToView();
    }

    // The front matter's targetSdk is the source of truth, like Kind: picking an SDK in the toolbar
    // writes it — "latest (…)" as `latest`, any other entry as its version (net8.0). Selecting what the
    // file already says when it opens (SetDocumentContent, _syncingSdk) isn't an edit.
    private bool _syncingSdk;

    private void OnSelectedSdkChanged(DotnetSdk? sdk)
    {
        // The new target's references, C# version and symbols apply to the text already in the
        // editor: analyse it again once they are in, as the squiggles would otherwise wait for the next edit.
        RefreshIntelliSenseContext(reanalyze: true);
        if (_syncingSdk || sdk is null || State.RpadDocument is not { } rpad) return;

        var target = ReferenceEquals(sdk, _vm.LatestSdk) ? TargetSdk.Default : TargetSdkResolver.ToTarget(sdk);
        if (target == rpad.FrontMatter.TargetSdk) return;
        rpad.FrontMatter.TargetSdk = target;
        UpdateProjectInfoBlock();
        CommitMetadataChange();
    }

    // "{Kind} · {TargetSdk}" status-bar readout for the currently open .rpad —
    // called on open (SetDocumentContent) and again whenever the kind combobox changes it.
    private void UpdateProjectInfoBlock()
        => ProjectInfoBlock.Text = State.RpadDocument is { } rpad
            ? $"{rpad.FrontMatter.Kind} · {rpad.FrontMatter.TargetSdk}{NotInstalledSuffix(rpad.FrontMatter.TargetSdk)}"
            : "";

    // The script runs on another SDK than its targetSdk names (RunAsync prints the warning).
    private string NotInstalledSuffix(TargetSdk target)
        => _vm.SelectedSdk is { } sdk && TargetSdkResolver.Warning(target, sdk) is not null ? " (not installed)" : "";

    // The click-time half of
    // the lightbulb (both diagnostic fixes and refactorings feed the same CodeFixEntry.Key/
    // FixApplyRequested path — see EditorsTabItemControl's own SetupEditor comment on the merged
    // list). MaterializeFixAsync only now does the expensive work (GetOperationsAsync + diffing) that
    // used to run eagerly for every candidate on every keystroke/caret-move; null means the key no
    // longer resolves (the code changed enough since this entry was listed that the underlying
    // RoslynCodeFixEngine cache moved on) — silently do nothing, same "gracefully degrade" contract
    // as every other IntelliSense failure path in this codebase, not worth surfacing as an error for
    // what's really just "you clicked something that's since gone stale".
    private async Task ApplyCodeFixAsync(CodeTextEditor editor, IIntelliSenseService intelliSense, CodeFixEntry fix)
    {
        var code = editor.Text;
        var materialized = await intelliSense.MaterializeFixAsync(code, fix.Key, CancellationToken.None);
        if (materialized is null) return;

        // MaterializeFixAsync's own Task.Run (CSharpIntelliSenseService) means this continuation
        // isn't guaranteed to resume on the UI thread — same not-implicit-by-default rationale as
        // every other post-Task.Run UI touch in this method (QuickInfo hover, refactoring caret-move
        // handler both explicitly marshal back too, right below this one).
        await Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;

            // Usings go to the .rpad front matter's `namespaces:` list (a project file, per
            // docs/ImplementationPlanRPad.md), not the visible script — except for a plain script
            // with no front matter to route them to, where they're folded into the text.
            var hasProjectFile = State.RpadDocument is not null;
            var newCode = hasProjectFile
                ? materialized.NewCode
                : PrependUsings(materialized.UsingsToAdd, materialized.NewCode);

            if (editor.Document.Text != newCode)
                editor.Document.Text = newCode;
            if (hasProjectFile && materialized.UsingsToAdd.Count > 0)
                AddGlobalUsings(materialized.UsingsToAdd);
        });
    }

    private static string PrependUsings(IReadOnlyList<string> usings, string code) =>
        usings.Count == 0 ? code : string.Concat(usings.Select(u => $"using {u};\n")) + code;

    // RunPad.Avalonia.CodeTextEditor.CodeFixSeverity duplicates
    // RunPad.Contracts.IntelliSense.DiagnosticSeverity rather than referencing it (the control stays
    // dependency-free from RunPad.Contracts — see CodeFixEntry's doc comment), so this is the one
    // place the two enums need mapping between them.
    private static CodeFixSeverity ToMarginSeverity(ContractSeverity severity) => severity switch
    {
        ContractSeverity.Error => CodeFixSeverity.Error,
        ContractSeverity.Warning => CodeFixSeverity.Warning,
        _ => CodeFixSeverity.Info,
    };

    // Defaults to C# for untitled tabs (no RpadDocument yet — see SetDocumentContent).
    private string ResolveLanguageId()
        => (State.RpadDocument?.FrontMatter.Language ?? RpadLanguage.CSharp).ToLanguageId();

    private static string FormatElapsed(TimeSpan t) => t switch
    {
        { TotalSeconds: < 1 } => $"{t.TotalMilliseconds:F0} ms",
        { TotalMinutes: < 1 } => $"{t.TotalSeconds:F2} s",
        _ => $"{(int)t.TotalMinutes}m {t.Seconds}s",
    };

    private static string EnsureWorkDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RunPad");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // What this tab's run asks for; its language's backend builds the host's own options from it.
    private ExecutionRequest BuildRunRequest(DotnetSdk platform, string workDir)
    {
        var rpad = State.RpadDocument;

        // Reused across runs of this tab (allocated once, normally already set by SetDocumentContent
        // at file-open time) so the process compiler can detect an unchanged project/source and skip
        // NuGet restore or the whole build — see its Compilation() for the actual checks. Untitled
        // tabs (no FilePath) have no stable identity to persist against, so they just get a
        // session-only random directory, same as before.
        if (_vm.FilePath is not null)
        {
            State.OutputDirectory ??= _buildCache.EnsureOutputDirectory(_vm.FilePath, workDir);
            _buildCache.Touch(_vm.FilePath);
        }
        else
        {
            State.OutputDirectory ??= Path.Combine(workDir, Path.GetRandomFileName());
        }

        // rpad is null for an untitled tab that's never been saved (see SetDocumentContent): the
        // request then has the defaults a fresh document has. The references are passed as written —
        // the backend resolves them relative to the script's folder with macros, as the CLI's and
        // IntelliSense's are.
        return ExecutionRequest.For(rpad?.FrontMatter, _vm.FilePath) with
        {
            Connections = GeneratedConnectionsFor(rpad?.FrontMatter),
            ReferenceBasePath = _vm.ReferenceAnchorPath,
            Platform = platform,
            OutputDirectory = State.OutputDirectory,
            WorkingDirectory = workDir,
            RuntimePath = _prefs.Debugger.DotnetPath,
            DebuggerPath = _prefs.Debugger.DebuggerPath,
            BreakOnAllExceptions = _vm.DebugPanel.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = _vm.DebugPanel.BreakOnUncaughtExceptions,
        };
    }

    // The client classes of the script's connections, for its build and for the editor alike.
    // Where relative paths in the script's connections (a database file) are read from: the
    // script's own folder, as the running script reads them through Util.CurrentQueryPath.
    private string? ScriptDirectory => _vm.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    private GeneratedConnections? GeneratedConnectionsFor(RpadFrontMatter? frontMatter) =>
        frontMatter is null ? null
            : GeneratedConnections.For(frontMatter.Connections, frontMatter.Language, frontMatter.Packages.Keys, _connectionDrivers,
                _connectionSchemas?.Cache, ScriptDirectory, readSchemaFromDisk: false);

    private sealed class ScriptBreakpoint(
        string fileName,
        int line,
        string? condition,
        string? hitCondition = null)
        : Contracts.IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition { get; } = condition;
        public string? HitCondition { get; } = hitCondition;
    }
}

/// <summary>How <see cref="EditorsTabItemControl.ReloadFromDisk"/> ended.</summary>
internal enum DiskReload
{
    Reloaded,
    /// <summary>The file is gone or can't be read; the tab is left as it was.</summary>
    Unavailable,
    /// <summary>The file now holds a script in another language; the tab is left as it was.</summary>
    OtherLanguage,
}
