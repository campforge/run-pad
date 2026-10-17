using System.Reactive.Linq;
using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ReactiveUI;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Build.Core.Models;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.VB;
using RunPad.Resources;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.Services.Scaling;
using RunPad.UserControls.Models;
using ContractSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.UserControls;

public partial class EditorsTabItemControl : UserControl, IDisposable
{
    private readonly ILanguageHostRegistry _registry;
    private readonly IPreferencesService _prefs;
    private readonly EditorTabItemViewModel _vm;
    private TabControl? _outputTabsControl;

    private readonly IThemeService _themeService;
    private readonly IWindowService _windowService;
    internal readonly EditorTabState State;
    private SemanticHighlighter? _semanticHighlighter;
    private InlayHintRenderer? _inlayHintRenderer;
    private IIntelliSenseService? _intelliSense;
    private bool _runPadAnalyzers;

    // Raw (unfiltered) lightbulb candidate lists — see UpdateLightbulb's own doc comment for why
    // filtering happens at render time instead of once when each list is produced.
    private List<CodeFixEntry> _diagnosticFixEntries = [];
    private List<CodeFixEntry> _refactoringFixEntries = [];
    private readonly CSharpReferenceService _referenceService;
    private readonly IBuildCacheService _buildCache;
    private IDisposable? _selectedKindSub;
    private IDisposable? _filePathSub;
    private string? _filePath;
    private IDisposable? _selectedSdkSub;
    private bool _disposed;

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
        CSharpReferenceService referenceService,
        IBuildCacheService buildCache)
    {
        _themeService = themeService;
        _windowService = windowService;
        _registry = registry;
        _prefs = prefs;
        _referenceService = referenceService;
        _buildCache = buildCache;
        _vm = vm;
        _vm.WriteToFile = WriteFile;
        DataContext = _vm;

        InitializeComponent();

        _outputTabsControl = this.FindControl<TabControl>("OutputTabsControl");
        var editor = new CodeTextEditor { Background = _themeService.GetBrush("RunPad.Editor.BackgroundBrush") };
        State = new EditorTabState(editor);
        ResultsOutput.DataContext = State.Output;
        State.Output.Css = HtmlThemeStylesheet.Generate(themeService.CurrentTheme.Interface.Variant,
            themeService.CurrentTheme.Interface.IsDark);

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

        var themeName = themeService.CurrentTheme.Editor.TextMateName;
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (s, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            var langExt = LangToExtension(State.RpadDocument?.FrontMatter.Language.ToString());
            editor.SetLanguage(themeName, langExt);
            ApplySelectionColor(editor);
        };
        editor.AttachedToVisualTree += onAttached;

        // Caret position → StatusLine
        editor.TextArea.Caret.PositionChanged += (_, _) =>
            StatusLine.Text = $"Line {editor.TextArea.Caret.Line}  Col {editor.TextArea.Caret.Column}";

        EditorHost.Content = editor;

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

    internal void ApplyTheme(ActiveTheme theme)
    {
        State.Editor.SetTheme(theme.Editor.TextMateName);
        _semanticHighlighter?.SetDark(theme.Editor.IsDark);
        var editorBg = _themeService!.GetBrush("RunPad.Editor.BackgroundBrush");

        if (editorBg is not null)
        {
            State.Editor.Background = editorBg;
        }

        ApplySelectionColor(State.Editor);
        State.Output.Css = HtmlThemeStylesheet.Generate(theme.Interface.Variant, theme.Interface.IsDark);
        UpdateOutputUi();
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
    internal async Task SaveAsync()
    {
        if (_vm.FilePath is null) return;
        var result = await InPlaceSave.SaveAsync(_vm, _windowService, async () => await _vm.SaveAsCommand.Execute());
        if (_disposed) return;
        if (result.Status == InPlaceSaveStatus.Failed)
            SetStatus($"Save failed: {result.Error.Replace('\n', ' ')}", "RunPad.Status.ErrorBrush");
        else if (result.Status == InPlaceSaveStatus.Cancelled && _vm.IsDirty)
            SetStatus("Not saved: the file was changed by another program", "RunPad.Status.CancelledBrush");
        UpdateOutputUi();
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
            // A script written to another folder (Save As) gets its relative references rewritten so
            // they still reach the same files; the tab adopts them only once it really moved there
            // (OnFilePathChanged) — a cancelled or failed Save As leaves its references as they were.
            var references = rpad.FrontMatter.References;
            if (references is not null && RebasedReferences(references, _vm.FilePath, path) is { } rebased)
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

    // The references as written for the script saved at newPath instead of oldPath, or null if
    // nothing changes (same folder, an untitled tab whose relative references had no folder yet, or
    // nothing relative).
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
        if (oldPath is not null
            && State.RpadDocument?.FrontMatter is { References: { } references } frontMatter
            && RebasedReferences(references, oldPath, newPath) is { } rebased)
        {
            frontMatter.References = rebased;
            _metadataBaseline = FrontMatterSnapshot();
        }
        RefreshIntelliSensePackages();
        _intelliSense?.NotifyTextChanged(State.Editor.Text);
    }

    internal void Cancel()
    {
        State.CurrentDebugCtx?.Resume(DebugAction.Continue);
        State.CurrentDebugCtx = null;
        _vm.DebugPanel.ClearStop();
        State.RunCts?.Cancel();
    }

    internal async Task RunAsync(bool debug, DotnetSdk platform)
    {
        var s = State;
        if (s.RunCts is { IsCancellationRequested: false }) return;

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

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.Output.Clear();
        s.DumpContainerBlocks.Clear();
        s.HadRunError = false;
        s.UnhandledDebugException = null;
        _vm.DebugPanel.ClearStop();
        s.ElapsedText = "";
        IlOutput.Text = "";
        SetStatus("Process", "RunPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        if (s.RpadDocument is { } doc && TargetSdkResolver.Warning(doc.FrontMatter.TargetSdk, platform) is { } sdkWarning)
            s.Output.Append($"\n<div>warning: {HttpUtility.HtmlEncode(sdkWarning)}</div>");
        UpdateOutputUi();

        if (_vm.FilePath is not null)
        {
            try
            {
                WriteFile(_vm.FilePath);
                _vm.IsDirty = false;
            }
            catch (Exception ex)
            {
                AppendError(new IOException($"Cannot save '{Path.GetFileName(_vm.FilePath)}': {ex.Message}", ex));
                _vm.RunState = RunState.Idle;
                s.RunStopwatch.Stop();
                UpdateOutputUi();
                return;
            }
        }

        s.RunCts?.Dispose();
        s.RunCts = new CancellationTokenSource();

        var cancelled = false;
        RunResult? runResult = null; // stays null for a debug run — DAP doesn't report an exit code here yet
        try
        {
            var workDir = EnsureWorkDirectory();
            var options = BuildRunOptions(platform, workDir);
            var script = s.Editor.Text;
            // Matches the path the language's compiler writes the source to and its DapSession
            // expects it at (OutputDirectory/{OutputName}.{extension}) — see ScriptBreakpoint below.
            var sourceFilePath = Path.Combine(
                State.OutputDirectory!, $"{LanguageHostOptionsDefaults.OutputName}{SourceExtension()}");
            s.SourceFilePath = sourceFilePath;
            var breakpoints = s.Editor.Breakpoints
                .Where(bp =>
                    bp.IsEnabled) // Disabled breakpoints stay in the gutter but never reach the debugger
                .Select(bp =>
                    (Contracts.IBreakpoint)new ScriptBreakpoint(sourceFilePath, bp.Line, bp.Condition, bp.HitCondition))
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
                await using var host = _registry.Create(ResolveLanguageId(), options);
                s.RunningHost = host;
                host.Compiled += binaryPath =>
                {
                    UpdateIlOutput(binaryPath);
                    // The build has just restored the packages: completion and diagnostics now see the
                    // versions and transitive packages it resolved.
                    Dispatcher.Post(RefreshIntelliSensePackages);
                };
                host.InputRequested += (prompt, isPassword) =>
                    Dispatcher.Post(() => ShowInputPrompt(prompt, isPassword));

                if (debug && host.Supports(LanguageHostCapabilities.Debug))
                {
                    foreach (var bp in breakpoints) host.AddBreakpoint(bp);
                    host.BreakpointHit += ctx =>
                        Dispatcher.Post(() => OnBreakpointHit(ctx));
                    host.BreakpointVerified += (bp, verified, message) =>
                        Dispatcher.Post(() => OnBreakpointVerified(bp, verified, message));
                    await host.RunDebugAsync(
                        script,
                        (data, kind) => Dispatcher.Post(() => AppendOutput(data, kind)),
                        s.RunCts.Token);
                }
                else
                {
                    runResult = await host.RunAsync(
                        script,
                        (data, kind) => Dispatcher.Post(() => AppendOutput(data, kind)),
                        s.RunCts.Token);
                }
            }, s.RunCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            AppendError(ex);
        }
        finally
        {
            s.RunningHost = null;
            await s.RunCts.CancelAsync();
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
                ReportRunFailure(failed);
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

    public void Dispose()
    {
        _disposed = true;
        _prefs.Changed -= OnPreferencesChanged;
        _viewScale.Changed -= OnViewScaleChanged;
        State.Editor.ZoomRequested -= OnEditorZoomRequested;
        _selectedKindSub?.Dispose();
        _filePathSub?.Dispose();
        _selectedSdkSub?.Dispose();
        _vm.DebugPanel.WatchExpressions.CollectionChanged -= OnWatchExpressionsCollectionChanged;
        Cancel();
        State.RunCts?.Dispose();
        _intelliSense?.Dispose(); // owned by this tab now — see EditorTabsControl.CreateControl
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

    private async void OnBreakpointRightClicked(int line, RunPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new Windows.ConditionDialog(bp?.Condition, bp?.HitCondition, bp?.IsEnabled ?? true);
        var result = await dialog.ShowDialog<(string? Condition, string? HitCondition, bool Enabled)?>(owner);
        if (result is null) return; // cancelled

        var condition = string.IsNullOrWhiteSpace(result.Value.Condition) ? null : result.Value.Condition;
        var hitCondition = string.IsNullOrWhiteSpace(result.Value.HitCondition) ? null : result.Value.HitCondition;
        var enabled = result.Value.Enabled;

        if (bp is not null)
        {
            bp.Condition = condition;
            bp.HitCondition = hitCondition;
            bp.IsEnabled = enabled;
            State.Editor.InvalidateBreakpoints();
        }
        else if (condition is not null || hitCondition is not null || !enabled)
        {
            State.Editor.AddBreakpoint(line);
            var newBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == line);
            if (newBp is not null)
            {
                newBp.Condition = condition;
                newBp.HitCondition = hitCondition;
                newBp.IsEnabled = enabled;
                State.Editor.InvalidateBreakpoints();
            }
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

        if (kind == OutputKind.StandardError)
            State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(data)}</div>");
        else
            State.Output.Append($"\n<div>{HttpUtility.HtmlEncode(data)}</div>");
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
    private void ReportRunFailure(RunResult result)
    {
        State.HadRunError = true;
        var (message, status) = result.Outcome switch
        {
            RunOutcome.CompilationFailed => (null, "Compilation failed"),
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

    private void AppendError(Exception ex)
    {
        State.HadRunError = true;
        State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>");
        if (ex.StackTrace is { } st)
            State.Output.Append($"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>");
        SetStatus("Error", "RunPad.Status.ErrorBrush");
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
    private void UpdateIlOutput(string binaryPath)
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

        Dispatcher.Post(() => IlOutput.Text = il);
    }

    // ILanguageHost.InputRequested (Util.ReadLine/
    // GetPassword). Always called on the UI thread (see the Dispatcher.Post at the
    // subscription site in RunAsync). internal (not private): lets tests drive the prompt directly,
    // the same rationale as BuildFindReferencesMenu (headless Avalonia has no reliable way to fake a
    // real running script asking for input).
    internal void ShowInputPrompt(string? prompt, bool isPassword)
    {
        InputPromptLabel.Text = string.IsNullOrEmpty(prompt) ? "Input:" : prompt;
        InputPromptBox.PasswordChar = isPassword ? '●' : default;
        InputPromptBox.Text = "";
        InputPromptBar.IsVisible = true;
        InputPromptBox.Focus();
    }

    internal void HideInputPrompt()
    {
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
        if (State.RunningHost is not { } host) return;

        var text = InputPromptBox.Text ?? "";
        var isPassword = InputPromptBox.PasswordChar != default;
        HideInputPrompt();

        // Echo what was submitted into the output, the same way a real console would — except a
        // password, which a real console never echoes either.
        if (!isPassword)
            AppendOutput(text, OutputKind.StandardOutput);

        await host.SendInputAsync(text, CancellationToken.None);
    }

    // Forwards gutter breakpoint edits to the running debug session, if there is one — otherwise
    // they only take effect the next time RunAsync reads State.Editor.Breakpoints at run start.
    private void OnGutterBreakpointAdded(object? sender, BreakpointEventArgs e)
    {
        if (State.RunningHost is not { } host || !host.Supports(LanguageHostCapabilities.Breakpoints)) return;
        if (State.SourceFilePath is not { } path) return;
        var editorBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == e.Line);
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

    private void OnPreferencesChanged()
        => Dispatcher.Post(() =>
        {
            ApplyEditorSettings(State.Editor, _prefs.Editor, _viewScale.CodeZoom);
            _prefs.Keyboard.ApplyTo(State.Editor);
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
            var ct = quickInfoCts.Token;
            var pos = e.GetPosition(editor);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(QuickInfoHoverDelay, ct);
                    var (offset, text) =
                        await Dispatcher.InvokeAsync(() => (editor.GetOffsetFromPoint(pos), editor.Text));
                    if (offset < 0 || ct.IsCancellationRequested) return;

                    // A diagnostic under the cursor takes priority over Quick Info — seeing why a
                    // squiggle is there is usually more useful than the symbol's signature.
                    var diagnostic =
                        squiggleRenderer.Diagnostics.FirstOrDefault(d =>
                            offset >= d.StartOffset && offset < d.EndOffset);
                    var quickInfo = diagnostic is null
                        ? await intelliSense.GetQuickInfoAsync(text, offset, ct)
                        : null;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;

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
            ToolTip.SetTip(editor.TextArea.TextView, null);
        };
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

    private void SetDocumentContent(CodeTextEditor editor, string? filePath)
    {
        string content;
        RpadDocument? rpad = null;

        if (filePath is null && _vm.Example is { } example)
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
            // Decoded as File.ReadAllText does; the exact bytes are what later saves compare with.
            var bytes = File.ReadAllBytes(filePath);
            _vm.DiskState = ScriptFileState.Of(bytes);
            string raw;
            using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                raw = reader.ReadToEnd();
            var parsed = RpadParser.Parse(raw);
            rpad = parsed.Document;
            // Invalid document: show the whole file as-is instead of default metadata —
            // WriteFile's no-RpadDocument branch then saves it back verbatim, and RunAsync refuses it.
            content = rpad?.RunCode ?? raw;
            State.DocumentParseError = parsed.Error;
            if (parsed.Error is { } error)
            {
                State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(InvalidDocumentMessage(error))}</div>");
                SetStatus("Invalid document", "RunPad.Status.ErrorBrush");
                UpdateOutputUi();
            }

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

        // Push kind/packages/usings into IntelliSense BEFORE handing the editor its text: setting
        // editor.Document below fires TextChanged synchronously, which is what actually triggers the
        // first NotifyTextChanged/diagnostics pass. Doing this the other way around (as it used to
        // be) meant that very first pass always ran against IntelliSense's stale defaults — kind
        // still RpadKind.Statements, no packages, no usings — since UpdateKind/UpdatePackageReferences/
        // UpdateGlobalUsings only invalidate the cached analysis document, they don't themselves
        // trigger a fresh pass. For a Program-kind file, that meant a spurious "declared but never
        // used" CS8321 on the user's own Main sitting in the diagnostics panel until their next
        // keystroke; for a file with front-matter packages/usings, spurious "type not found"
        // squiggles for anything only resolvable through them.
        RefreshIntelliSenseKind();
        RefreshIntelliSensePackages();
        if (rpad?.FrontMatter.Namespaces is { Count: > 0 })
            RefreshIntelliSenseUsings();

        if (_vm.Example is not null && filePath is null)
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

    /// <summary>
    /// Pushes this tab's current NuGet packages into its own IntelliSense instance, with where this
    /// tab builds, so it can use the assemblies the last restore resolved. Called on initial load,
    /// after the Dependencies window closes (it mutates the same RpadFrontMatter in place without any
    /// change notification of its own), after each successful build and when the SDK changes.
    /// </summary>
    internal void RefreshIntelliSensePackages()
    {
        if (_disposed || _intelliSense is not { } intelliSense) return;
        var packages = State.RpadDocument?.FrontMatter.Packages ?? new Dictionary<string, Package>();
        var targetFramework = (_vm.SelectedSdk ?? _vm.AvailableSdks.FirstOrDefault())?.TargetFrameworkMoniker;
        if (targetFramework is null)
        {
            intelliSense.UpdatePackageReferences(packages);
            return;
        }
        var references = ReferencePathResolver.Resolve(State.RpadDocument?.FrontMatter.References, _vm.FilePath);
        intelliSense.UpdatePackageReferences(packages, new PackageReferenceContext(
            targetFramework, State.OutputDirectory, RunPadNuGetConfigStore.ExistingDefaultConfigFile, _prefs.Debugger?.DotnetPath,
            references.Assemblies));
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
        var packages = fm.Packages is null
            ? ""
            : string.Join(";", fm.Packages.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Version}"));
        return string.Join("\n",
            fm.Version, fm.Language, fm.TargetSdk, fm.Kind, packages,
            string.Join(";", fm.References ?? []), string.Join(";", fm.Namespaces ?? []));
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
        RefreshIntelliSensePackages();
        // The new target's references, C# version and symbols apply to the text already in the
        // editor: analyse it again now, as the squiggles would otherwise wait for the next edit.
        if (!_disposed) _intelliSense?.NotifyTextChanged(State.Editor.Text);
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

    private static string LangToExtension(string? language) => language?.ToLowerInvariant() switch
    {
        "vb" => ".vb",
        _ => ".cs",
    };

    // Defaults to C# for untitled tabs (no RpadDocument yet — see SetDocumentContent).
    private string ResolveLanguageId()
        => (State.RpadDocument?.FrontMatter.Language ?? RpadLanguage.CSharp).ToLanguageId();

    private string SourceExtension() => LangToExtension(State.RpadDocument?.FrontMatter.Language.ToString());

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

    private ILanguageHostOptions BuildRunOptions(DotnetSdk platform, string workDir)
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

        var executionOptions = new ExecutionOptions
        {
            WorkingDirectory = workDir,
            Runtime = new RuntimeOptions { RuntimePath = _prefs.Debugger.DotnetPath },
            Debugger = new DebuggerOptions { DebuggerPath = _prefs.Debugger.DebuggerPath },
        };

        // rpad is null for an untitled tab that's never been saved (see SetDocumentContent) —
        // fall back to the same defaults RpadWriter.NewFile/RpadParser use for a fresh document.
        var kind = rpad?.FrontMatter.Kind ?? RpadKind.Statements;
        var packages = rpad?.FrontMatter.Packages.ToDictionary(v => v.Key, v => v.Value.Version) ?? [];
        var usings = rpad?.FrontMatter.Namespaces ?? [];
        // Relative to the script's folder, macros expanded — the same resolution the CLI and
        // IntelliSense use. Project references aren't supported any more (scripts compile in-process,
        // no MSBuild): they're still collected so the compiler reports them as an error.
        var references = ReferencePathResolver.Resolve(rpad?.FrontMatter.References, _vm.FilePath);

        // One options type for every compiled .NET language — used to be a
        // per-language if/else building two byte-for-byte identical option objects under different
        // type names.
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = platform,
            ExecutionOptions = executionOptions,
            OutputDirectory = State.OutputDirectory,
            RuntimeLibPath = _referenceService.GetExtensionReference(),
            BreakOnAllExceptions = _vm.DebugPanel.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = _vm.DebugPanel.BreakOnUncaughtExceptions,
            Kind = kind,
            Packages = packages,
            Usings = usings,
            AssemblyReferences = references.Assemblies,
            ProjectReferences = references.Projects,
            ReferenceErrors = references.Errors,
            ReferenceWarnings = references.Warnings,
            CurrentQueryPath = _vm.FilePath,
            NuGetConfigFile = RunPadNuGetConfigStore.ExistingDefaultConfigFile,
        };
    }

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
