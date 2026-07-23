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
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Avalonia.Themes;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.VB;
using DotPad.Resources;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using ReactiveUI;
using ContractSeverity = DotPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace DotPad.UserControls;

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

    // Raw (unfiltered) lightbulb candidate lists — see UpdateLightbulb's own doc comment for why
    // filtering happens at render time instead of once when each list is produced.
    private List<CodeFixEntry> _diagnosticFixEntries = [];
    private List<CodeFixEntry> _refactoringFixEntries = [];
    private readonly CSharpReferenceService _referenceService;
    private readonly IBuildCacheService _buildCache;
    private IDisposable? _selectedKindSub;
    private bool _disposed;

    // Debounce before firing a Quick Info request after the pointer stops moving — long enough
    // that just passing over a symbol doesn't spam requests, short enough to still feel responsive
    // (ANALYSIS.md #94).
    private static readonly TimeSpan QuickInfoHoverDelay = TimeSpan.FromMilliseconds(400);

    // Same rationale as QuickInfoHoverDelay, for refactorings (TODO.md #74) — caret moves far more
    // often than the pointer stops over a symbol (every arrow key press, every click), so this needs
    // the same debounce discipline diagnostics/inlay hints already get from NotifyTextChanged's own
    // 500ms — refactorings just can't share that pass (see GetRefactoringsAsync's own doc comment on
    // IIntelliSenseService: availability depends on caret position, not on what changed in the text).
    private static readonly TimeSpan RefactoringDebounceDelay = TimeSpan.FromMilliseconds(400);

    internal EditorsTabItemControl(
        EditorTabItemViewModel vm,
        IThemeService themeService,
        IWindowService windowService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IIntelliSenseService? intelliSense,
        CSharpReferenceService referenceService,
        IBuildCacheService buildCache,
        IBrush? editorBackground)
    {
        _themeService = themeService;
        _windowService = windowService;
        _registry = registry;
        _prefs = prefs;
        _referenceService = referenceService;
        _buildCache = buildCache;
        _vm = vm;
        DataContext = _vm;

        InitializeComponent();

        _outputTabsControl = this.FindControl<TabControl>("OutputTabsControl");

        var editor = new CodeTextEditor { Background = editorBackground };
        State = new EditorTabState(editor);
        ResultsOutput.DataContext = State.Output;
        State.Output.Css = HtmlThemeStylesheet.Generate(themeService.CurrentTheme.Interface.Variant,
            themeService.CurrentTheme.Interface.IsDark);

        ApplyEditorSettings(editor, prefs.Editor);
        prefs.Changed += OnPreferencesChanged;

        _intelliSense = intelliSense;
        SetupEditor(editor, intelliSense);
        editor.BreakpointAdded += OnGutterBreakpointAdded;
        editor.BreakpointRemoved += OnGutterBreakpointRemoved;

        _selectedKindSub = _vm.WhenAnyValue(x => x.SelectedKind).Subscribe(OnSelectedKindChanged);

        // Load document content first, without TextMate.
        // SetLanguage calls Redraw() internally, which crashes when the editor
        // is not yet in the visual tree (no visual lines exist yet).
        // We defer SetLanguage to AttachedToVisualTree so Redraw() works on the
        // first call and highlighting appears immediately, without NullReferenceException.
        SetDocumentContent(editor, _vm.FilePath);

        // Dirty indicator (TODO.md, "Жизненный цикл вкладок") — attached only AFTER the initial
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
        foreach (var (keyword, template) in _registry.GetSnippets(ResolveLanguageId()) ?? new Dictionary<string, string>())
            editor.Snippets[keyword] = template;
        ApplyEditorLanguageProfile(editor, _registry.GetEditorProfile(ResolveLanguageId()) ?? new EditorLanguageProfile());

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
        _vm.DebugPanel.WatchExpressions.CollectionChanged += (_, e) =>
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
        };
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
        var editorBg = _themeService!.GetBrush("DotPad.Editor.BackgroundBrush");

        if (editorBg is not null)
        {
            State.Editor.Background = editorBg;
        }

        ApplySelectionColor(State.Editor);
        State.Output.Css = HtmlThemeStylesheet.Generate(theme.Interface.Variant, theme.Interface.IsDark);
        UpdateOutputUi();
    }

    // Deliberately a no-op for a tab with no FilePath yet (TODO.md, "Жизненный цикл вкладок") — an
    // untitled, never-saved tab has nowhere to write to; it only gets a path (and starts being
    // covered by this method) via Save As (MainWindowViewModel.SaveTabAsAsync), never implicitly.
    internal void SaveIfNeeded()
    {
        if (_vm.FilePath is null) return;
        try
        {
            WriteFile(_vm.FilePath);
            _vm.IsDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Called on every text change/tab switch/close/app exit — silently eating a real write
            // failure (disk full, permissions, file deleted/locked externally) meant the user never
            // found out their edits weren't persisted. Surface it via the same status indicator
            // RunAsync uses for Success/Error/Cancelled, rather than losing it entirely.
            SetStatus($"Save failed: {ex.Message}", "DotPad.Status.ErrorBrush");
        }
    }

    // internal (not private): reused by MainWindowViewModel.SaveTabAsAsync/DuplicateTab (via
    // EditorTabsControl, which wires the tab's SaveAsCommand/DuplicateCommand to it) to write this
    // tab's current content to a path other than _vm.FilePath — the exact same serialization
    // SaveIfNeeded already uses for the normal save path, just parameterized on the target path.
    internal void WriteFile(string path)
    {
        if (State.RpadDocument is { } rpad)
        {
            rpad.RunCode = State.Editor.Text;
            File.WriteAllText(path, RpadWriter.Write(rpad), Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(path, State.Editor.Text);
        }
    }

    internal void Cancel()
    {
        State.CurrentDebugCtx?.Resume(DebugAction.Continue);
        State.CurrentDebugCtx = null;
        State.RunCts?.Cancel();
    }

    internal async Task RunAsync(bool debug, DotnetSdk platform)
    {
        var s = State;
        if (s.RunCts is { IsCancellationRequested: false }) return;

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.Output.Clear();
        s.DumpContainerBlocks.Clear();
        s.HadRunError = false;
        s.ElapsedText = "";
        IlOutput.Text = "";
        SetStatus("Process", "DotPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
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
                .Where(bp => bp.IsEnabled) // TODO.md #83.3 — disabled breakpoints stay in the gutter but never reach the debugger
                .Select(bp => (Contracts.IBreakpoint)new ScriptBreakpoint(sourceFilePath, bp.Line, bp.Condition, bp.HitCondition))
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
                host.Compiled += binaryPath => UpdateIlOutput(binaryPath);
                host.InputRequested += (prompt, isPassword) =>
                    Dispatcher.UIThread.Post(() => ShowInputPrompt(prompt, isPassword));

                if (debug && host.Supports(LanguageHostCapabilities.Debug))
                {
                    foreach (var bp in breakpoints) host.AddBreakpoint(bp);
                    host.BreakpointHit += ctx =>
                        Dispatcher.UIThread.Post(() => OnBreakpointHit(ctx));
                    host.BreakpointVerified += (bp, verified, message) =>
                        Dispatcher.UIThread.Post(() => OnBreakpointVerified(bp, verified, message));
                    await host.RunDebugAsync(
                        script,
                        (data, kind) => Dispatcher.UIThread.Post(() => AppendOutput(data, kind)),
                        s.RunCts.Token);
                }
                else
                {
                    await host.RunAsync(
                        script,
                        (data, kind) => Dispatcher.UIThread.Post(() => AppendOutput(data, kind)),
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

            if (cancelled)
            {
                SetStatus("Cancelled", "DotPad.Status.CancelledBrush");
            }
            else if (!s.HadRunError)
            {
                SetStatus("Success", "DotPad.Status.SuccessBrush");
            }

            UpdateOutputUi();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _prefs.Changed -= OnPreferencesChanged;
        _selectedKindSub?.Dispose();
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

    private async void DbgPause_Click(object? sender, RoutedEventArgs e)
    {
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
            // async void — an uncaught exception here would crash the whole process (ANALYSIS.md
            // #90). Surface it the same way a failed Run does, instead of losing it entirely.
            AppendError(ex);
        }
    }

    private async void DbgRunToCursor_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ctx = State.CurrentDebugCtx;
            if (ctx is null) return;
            var line = State.Editor.TextArea.Caret.Line;
            State.CurrentDebugCtx = null;
            _vm.RunState = RunState.RunDebug;
            State.Editor.DebugCurrentLine = null;
            SetStatus("Process", "DotPad.Status.RunningBrush");
            UpdateOutputUi();
            await ctx.RunToCursorAsync(line);
        }
        catch (OperationCanceledException)
        {
            // The run stopped on its own while Run to Cursor was in flight — not a real failure.
        }
        catch (Exception ex)
        {
            // async void — an uncaught exception here would crash the whole process (ANALYSIS.md
            // #90). Surface it the same way a failed Run does, instead of losing it entirely.
            AppendError(ex);
        }
    }

    private async void OnBreakpointRightClicked(int line, DotPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
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

    // TODO.md #62 "умное переименование" — F2 (CodeTextEditor.RenameSymbolRequested).
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
    internal static void ApplyRenameEdits(TextDocument document, IReadOnlyList<IDocumentHighlightSpan> spans, string newName)
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

    // TODO.md #62 "find references" — Shift+F12 (CodeTextEditor.FindReferencesRequested). Reuses
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

    // TODO.md #62 "Go to Definition"/"Go to Implementation" (F12, user request 2026-07-19) —
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

        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new Windows.AssemblyExplorerWindow(_themeService, _windowService, decompiled, definition.AssemblyPath);
        if (owner is not null) window.Show(owner);
        else window.Show();
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
        SetStatus("Process", "DotPad.Status.RunningBrush");
        var ctx = State.CurrentDebugCtx;
        State.CurrentDebugCtx = null;
        ctx?.Resume(action);
        UpdateOutputUi();
    }

    private void UpdateOutputUi()
    {
        ExecStatusBlock.Text = State.ExecStatusText;
        ExecStatusBlock.Foreground = string.IsNullOrEmpty(State.ExecStatusColor)
            ? Brushes.Transparent
            : _themeService.GetBrush(State.ExecStatusColor) ?? Brushes.Transparent;
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

            // DumpContainer (ANALYSIS.md feature #19) re-emits the same data-dp-id on every
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

    private void AppendError(Exception ex)
    {
        State.HadRunError = true;
        State.Output.Append($"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>");
        if (ex.StackTrace is { } st)
            State.Output.Append($"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>");
        SetStatus("Error", "DotPad.Status.ErrorBrush");
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

        Dispatcher.UIThread.Post(() => IlOutput.Text = il);
    }

    // TODO.md #63 "интерактивный типизированный ввод" — ILanguageHost.InputRequested (Util.ReadLine/
    // GetPassword). Always called on the UI thread (see the Dispatcher.UIThread.Post at the
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

    private void OnBreakpointHit(IBreakpointHitContext context)
    {
        var s = State;
        s.CurrentDebugCtx = context;
        _vm.RunState = RunState.Paused;
        State.Editor.DebugCurrentLine = context.CallStack.Count > 0
            ? context.CallStack[0].Line
            : context.Breakpoint.Line;

        // ToDo: перенести это все в DebugPanel
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

        SetStatus("Paused", "DotPad.Status.PausedBrush");
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
        => Dispatcher.UIThread.Post(() =>
        {
            ApplyEditorSettings(State.Editor, _prefs.Editor);

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

    // TODO.md #74 (user request 2026-07-21) — filters the merged diagnostic-fix + refactoring lists
    // against Preferences → C# → "code fixes enabled"/excluded-diagnostic-id settings at RENDER time,
    // not when each source list is produced: CodeFixesChanged/GetRefactoringsAsync only fire on their
    // own triggers (text change / caret move), so filtering there would leave a stale, unfiltered
    // lightbulb visible until the next one of those happens to fire. Filtering here instead means
    // OnPreferencesChanged can just call this directly to reflect a settings change immediately.
    //
    // Сто шестой проход (2026-07-21) switched exclusion from a Title substring match to an exact,
    // case-insensitive match against DiagnosticId ("CS0184", "IDE0007", ...) — a stable, documented
    // Roslyn identifier instead of prose a provider could phrase however it likes, per user request.
    // Accepted narrowing versus сто пятый проход's title-based filter: a refactoring's DiagnosticId
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
            merged = merged.Where(f => f.DiagnosticId is null || !excluded.Contains(f.DiagnosticId, StringComparer.OrdinalIgnoreCase));

        State.Editor.UpdateCodeFixes([.. merged]);
    }

    private static void ApplySelectionColor(CodeTextEditor editor)
    {
        // TextMate may set SelectionBrush to a dark color even for light themes.
        // Read the brush from the current theme so it's fully configurable per theme.
        var variant = Application.Current?.ActualThemeVariant;
        if (Application.Current?.TryGetResource("DotPad.Editor.SelectionBrush", variant, out var res) == true
            && res is IBrush brush)
            editor.TextArea.SelectionBrush = brush;
        editor.TextArea.SelectionForeground = null;
    }

    // Test-only observability (InternalsVisibleTo), same "internal test hook" pattern used elsewhere
    // in the solution — lets a test confirm settings (notably FontFamily, TODO.md #112) actually reach
    // the real CodeTextEditor without needing to construct a whole EditorTabItemControl.
    internal static void ApplyEditorSettings(CodeTextEditor editor, EditorSettings s)
    {
        editor.ShowLineNumberText = s.ShowLineNumbers;
        editor.ShowFolding = s.ShowFolding;
        editor.FontSize = s.FontSize;
        editor.FontFamily = new FontFamily(s.FontFamily);
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
            // TODO.md #65: a genuine Ctrl+Space press (as opposed to the 24/7 auto-invoke/generics/
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
            var list = await intelliSense.GetCompletionsAsync(State.Editor.Text, e.CaretOffset, trigger, e.CancellationToken);
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
                // TODO.md item 60 point 8 — an override-member stub carries a multi-line snippet
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
                    // Auto-import (TODO.md #28 remainder): non-empty only for a type from an added
                    // NuGet package that isn't imported yet — accepting it adds the using the same
                    // way the lightbulb already does (AddGlobalUsings), just without that extra step.
                    tags: item.UsingsToAdd,
                    onCommit: AddGlobalUsings,
                    // Item 60 points 6/7 / TODO #59 remainder: the list row itself gets a dimmed
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
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                squiggleRenderer.Update(diagnostics);
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            });

        // ── Semantic highlighting ─────────────────────────────────────────────
        var semanticHighlighter = new SemanticHighlighter();
        semanticHighlighter.SetDark(_themeService.CurrentTheme.Editor.IsDark);
        editor.TextArea.TextView.LineTransformers.Add(semanticHighlighter);
        _semanticHighlighter = semanticHighlighter;

        intelliSense.SemanticTokensChanged += (code, tokens) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                semanticHighlighter.Update(tokens);
                editor.TextArea.TextView.Redraw();
            });

        // ── Inlay hints (inferred `var`/`foreach (var ...)` types) ─────────────
        var inlayHintRenderer = new InlayHintRenderer { IsEnabled = _prefs.CSharp.InlayHintsEnabled };
        editor.TextArea.TextView.ElementGenerators.Add(inlayHintRenderer);
        _inlayHintRenderer = inlayHintRenderer;

        intelliSense.InlayHintsChanged += (code, hints) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                inlayHintRenderer.Update(hints);
                editor.TextArea.TextView.Redraw();
            });

        // ── Code fixes (lightbulb: real Roslyn CodeFixProviders + CodeRefactoringProviders) ─────
        // Two independent sources feed the same lightbulb margin (TODO.md #74): diagnostic-driven
        // fixes (CodeFixesChanged, pushed from NotifyTextChanged's background pass whenever the text
        // changes) and caret-position-driven refactorings (GetRefactoringsAsync, pulled below on
        // caret move — a refactoring isn't tied to any diagnostic, so it has no reason to recompute
        // just because the text changed elsewhere in the document). CodeFixMargin itself is unaware
        // of the split — it just renders whatever flat list UpdateLightbulb hands it — so each source
        // keeps its own last-known list here (as instance fields, not locals — OnPreferencesChanged
        // needs to re-filter+re-render the CURRENT lists the instant the user flips a code-fix
        // preference, not only on the next keystroke/caret-move) and remerges on every update from
        // either one.

        // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход) — just Line/Title/
        // Severity/Key now, no NewCode/UsingsToAdd to fold in here: materializing those got deferred
        // to ApplyCodeFixAsync below, run only for the one entry actually clicked, not eagerly for
        // every candidate on every keystroke/caret-move (see ICodeFixInfo's own doc comment for why
        // that used to dominate the whole IntelliSense pipeline's cost).
        static List<CodeFixEntry> ToEntries(IReadOnlyList<ICodeFixInfo> fixes) =>
            fixes.Select(f => new CodeFixEntry(f.Line, f.Title, ToMarginSeverity(f.Severity), f.Key, f.DiagnosticId)).ToList();

        intelliSense.CodeFixesChanged += (code, fixes) =>
            Dispatcher.UIThread.Post(() =>
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

            // User request 2026-07-21 (TODO.md #74) — the master "show code fixes" switch skips even
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
            var text = editor.Text;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RefactoringDebounceDelay, ct);
                    var refactorings = await intelliSense.GetRefactoringsAsync(text, offset, ct);
                    if (ct.IsCancellationRequested) return;

                    await Dispatcher.UIThread.InvokeAsync(() =>
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

        editor.TextChanged += (_, _) => intelliSense.NotifyTextChanged(editor.Text);

        // ── Quick Info tooltips on hover ──────────────────────────────────────
        ToolTip.SetShowDelay(editor.TextArea.TextView, 0);
        var quickInfoCts = new CancellationTokenSource();

        editor.TextArea.TextView.PointerMoved += (_, e) =>
        {
            // Not disposed here — a superseded hover's background Task.Run may still be mid-flight
            // past this point, and registering/reading against an already-disposed
            // CancellationTokenSource throws ObjectDisposedException (ANALYSIS.md #89). A
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
                    var (offset, text) = await Dispatcher.UIThread.InvokeAsync(
                        () => (editor.GetOffsetFromPoint(pos), editor.Text));
                    if (offset < 0 || ct.IsCancellationRequested) return;

                    // A diagnostic under the cursor takes priority over Quick Info — seeing why a
                    // squiggle is there is usually more useful than the symbol's signature.
                    var diagnostic =
                        squiggleRenderer.Diagnostics.FirstOrDefault(d =>
                            offset >= d.StartOffset && offset < d.EndOffset);
                    var quickInfo = diagnostic is null
                        ? await intelliSense.GetQuickInfoAsync(text, offset, ct)
                        : null;

                    await Dispatcher.UIThread.InvokeAsync(() =>
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
    // a single hardcoded C#-shaped profile applied to every language including VB (TODO.md #58); this
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
        // a time into a fresh default-comparer HashSet would silently drop it (found in code review).
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

        // TODO.md #76: no more switch on a FoldingStrategyKind enum naming a specific concrete type
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
        if (!string.IsNullOrEmpty(activeParameter?.Summary)) parts.Add($"{activeParameter.Name}: {activeParameter.Summary}");
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private void SetDocumentContent(CodeTextEditor editor, string? filePath)
    {
        string content;
        RpadDocument? rpad = null;

        if (filePath is null)
        {
            // Deferred creation (TODO.md, "Жизненный цикл вкладок") — a brand-new tab is genuinely
            // empty, not the old "Hello, RPad!" sample, and stays in-memory only until the user does
            // Save/Save As. It still gets a real (in-memory) RpadDocument built from the language/kind
            // chosen in NewFileKindDialog (_vm.NewFileLanguage/NewFileKind) rather than staying null —
            // that's what lets the toolbar's kind combobox, IntelliSense's kind-aware analysis, and
            // Run/Debug all already behave correctly for the chosen language/kind before the first
            // save, the same as they do for a file freshly loaded from disk.
            content = "";
            rpad = RpadParser.TryParse(RpadWriter.NewFile(
                title: "Untitled", language: _vm.NewFileLanguage, kind: _vm.NewFileKind, code: content));
        }
        else if (File.Exists(filePath))
        {
            var raw = File.ReadAllText(filePath);
            rpad = RpadParser.TryParse(raw);
            content = rpad?.RunCode ?? raw;

            // Allocate/reuse this file's persistent build directory now, at open time, rather than
            // lazily on first Run — see EnsureWorkDirectory/BuildRunOptions for how it's consumed.
            State.OutputDirectory = _buildCache.EnsureOutputDirectory(filePath, EnsureWorkDirectory());
        }
        else
        {
            content = "";
        }

        State.RpadDocument = rpad;
        _vm.FrontMatter = rpad?.FrontMatter;
        _vm.SelectedKind = rpad?.FrontMatter.Kind.ToString() ?? "Statements";

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
        if (rpad?.FrontMatter.Packages is { Count: > 0 })
            RefreshIntelliSensePackages();
        if (rpad?.FrontMatter.Namespaces is { Count: > 0 })
            RefreshIntelliSenseUsings();

        editor.Document = new TextDocument(content);

        // Shows which kind/target framework the script actually runs as (archived ANALYSIS.md #29) —
        // helps tell the two compile paths apart and diagnose compile/run discrepancies without
        // having to open the front matter by hand. Was left commented out with the XAML control
        // itself missing (ANALYSIS.md #95) — both restored.
        UpdateProjectInfoBlock();
    }

    /// <summary>
    /// Pushes this tab's current NuGet packages into its own IntelliSense instance. Called on initial
    /// load and again after the Dependencies window closes, since it mutates the same RpadFrontMatter
    /// in place without any change notification of its own.
    /// </summary>
    internal void RefreshIntelliSensePackages()
        => _intelliSense?.UpdatePackageReferences(State.RpadDocument?.FrontMatter.Packages ??
                                                  new Dictionary<string, Package>());

    /// <summary>Pushes this tab's front-matter `namespaces:` list into its own IntelliSense instance.</summary>
    private void RefreshIntelliSenseUsings()
        => _intelliSense?.UpdateGlobalUsings(State.RpadDocument?.FrontMatter.Namespaces ?? []);

    // Pushes this tab's front-matter `kind:` into its own IntelliSense instance, unconditionally
    // (unlike Packages/Namespaces above, Kind always has a meaningful value — there's no "empty" case
    // to skip) — so live analysis mirrors the same kind-specific transform the compiler applies
    // (ANALYSIS.md TODO #66).
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
        SaveIfNeeded();
    }

    // FrontMatter.Kind is the source of truth (like usings/packages/references above) — SelectedKind
    // is just the combobox's mirror of it. Guards against writing on the initial sync in
    // SetDocumentContent (which sets SelectedKind from the same value already on FrontMatter.Kind)
    // and only reacts to genuine user-driven changes. See ANALYSIS.md #35.
    private void OnSelectedKindChanged(string? kind)
    {
        var rpad = State.RpadDocument;
        if (rpad is null || kind is null) return;
        if (!Enum.TryParse(kind, out RpadKind parsedKind) || rpad.FrontMatter.Kind == parsedKind) return;

        rpad.FrontMatter.Kind = parsedKind;
        RefreshIntelliSenseKind();
        UpdateProjectInfoBlock();
        _intelliSense?.NotifyTextChanged(State.Editor.Text);
        SaveIfNeeded();
    }

    // "{Kind} · {TargetSdk}" status-bar readout for the currently open .rpad (ANALYSIS.md #95) —
    // called on open (SetDocumentContent) and again whenever the kind combobox changes it.
    private void UpdateProjectInfoBlock()
        => ProjectInfoBlock.Text = State.RpadDocument is { } rpad
            ? $"{rpad.FrontMatter.Kind} · {rpad.FrontMatter.TargetSdk}"
            : "";

    // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход) — the click-time half of
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed) return;
            if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;

            // Usings go to the .rpad front matter's `namespaces:` list (a project file, per
            // docs/ImplementationPlanRPad.md), not the visible script — except for a plain script
            // with no front matter to route them to, where they're folded into the text.
            var hasProjectFile = State.RpadDocument is not null;
            var newCode = hasProjectFile ? materialized.NewCode : PrependUsings(materialized.UsingsToAdd, materialized.NewCode);

            if (editor.Document.Text != newCode)
                editor.Document.Text = newCode;
            if (hasProjectFile && materialized.UsingsToAdd.Count > 0)
                AddGlobalUsings(materialized.UsingsToAdd);
        });
    }

    private static string PrependUsings(IReadOnlyList<string> usings, string code) =>
        usings.Count == 0 ? code : string.Concat(usings.Select(u => $"using {u};\n")) + code;

    // TODO.md #75: DotPad.Avalonia.CodeTextEditor.CodeFixSeverity duplicates
    // DotPad.Contracts.IntelliSense.DiagnosticSeverity rather than referencing it (the control stays
    // dependency-free from DotPad.Contracts — see CodeFixEntry's doc comment), so this is the one
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
        var dir = Path.Combine(Path.GetTempPath(), "DotPad");
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
        // $(RuntimeDirectory)/$(MyDocuments)/$(CurrentQueryPath) macros (TODO.md #63) — expanded
        // here so both the debug and non-debug run paths get them, since they share this method.
        var assemblyReferences = rpad?.FrontMatter.References?
            .Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(r => ReferencePathMacros.Expand(r, _vm.FilePath))
            .ToList() ?? [];
        // Both .csproj and .vbproj — a VB script's own project-reference should work the same as a
        // C# one's does (this used to only match .csproj, silently dropping .vbproj references for
        // VB scripts even though CliRunner.BuildOptions already matched both — ANALYSIS.md #91).
        var projectReferences = rpad?.FrontMatter.References?
            .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || r.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .Select(r => ReferencePathMacros.Expand(r, _vm.FilePath))
            .ToList() ?? [];

        // One options type for every compiled .NET language (ANALYSIS.md #86/#87) — used to be a
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
            AssemblyReferences = assemblyReferences,
            ProjectReferences = projectReferences,
            CurrentQueryPath = _vm.FilePath,
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
