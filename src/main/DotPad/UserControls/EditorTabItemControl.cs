using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
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
using DotPad.Languages.VB;
using DotPad.Resources;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;
using ReactiveUI;

namespace DotPad.UserControls;

public partial class EditorsTabItemControl : UserControl, IDisposable
{
    private readonly ILanguageHostRegistry _registry;
    private readonly IPreferencesService _prefs;
    private readonly EditorTabItemViewModel _vm;
    private TabControl? _outputTabsControl;

    private readonly IThemeService _themeService;
    internal readonly EditorTabState State;
    private SemanticHighlighter? _semanticHighlighter;
    private IIntelliSenseService? _intelliSense;
    private readonly CSharpReferenceService _referenceService;
    private readonly IBuildCacheService _buildCache;
    private IDisposable? _selectedKindSub;
    private bool _disposed;

    internal EditorsTabItemControl(
        EditorTabItemViewModel vm,
        IThemeService themeService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IIntelliSenseService? intelliSense,
        CSharpReferenceService referenceService,
        IBuildCacheService buildCache,
        IBrush? editorBackground)
    {
        _themeService = themeService;
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

    internal void SaveIfNeeded()
    {
        if (_vm.FilePath is null) return;
        try
        {
            WriteFile(_vm.FilePath);
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

    private void WriteFile(string path)
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
        // ToDo: сделать отдельный проект rpad ранннер, который будет запускать код с консоли без отладки
        var s = State;
        if (s.RunCts is { IsCancellationRequested: false }) return;

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.Output.Content = "";
        s.DumpContainerBlocks.Clear();
        s.HadRunError = false;
        s.ElapsedText = "";
        SetStatus("Process", "DotPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        UpdateOutputUi();

        if (_vm.FilePath is not null)
        {
            try
            {
                WriteFile(_vm.FilePath);
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
            // OutputName is fixed ("RunPadQuery") on every language's options type.
            var sourceFilePath = Path.Combine(State.OutputDirectory!, $"RunPadQuery{SourceExtension()}");
            s.SourceFilePath = sourceFilePath;
            var breakpoints = s.Editor.Breakpoints
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

            _vm.RunState = RunState.Idle;
            State.Editor.DebugCurrentLine = null;
            s.RunStopwatch.Stop();
            s.ElapsedText = FormatElapsed(s.RunStopwatch.Elapsed);

            if (cancelled) SetStatus("Cancelled", "DotPad.Status.CancelledBrush");
            else if (!s.HadRunError) SetStatus("Success", "DotPad.Status.SuccessBrush");

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
        catch (Exception)
        {
            throw; // TODO handle exception
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
        catch (Exception)
        {
            throw; // TODO handle exception
        }
    }

    private async void OnBreakpointRightClicked(int line, DotPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new Windows.ConditionDialog(bp?.Condition, bp?.HitCondition);
        var result = await dialog.ShowDialog<(string? Condition, string? HitCondition)?>(owner);
        if (result is null) return; // cancelled

        var condition = string.IsNullOrWhiteSpace(result.Value.Condition) ? null : result.Value.Condition;
        var hitCondition = string.IsNullOrWhiteSpace(result.Value.HitCondition) ? null : result.Value.HitCondition;

        if (bp is not null)
        {
            bp.Condition = condition;
            bp.HitCondition = hitCondition;
        }
        else if (condition is not null || hitCondition is not null)
        {
            State.Editor.AddBreakpoint(line);
            var newBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == line);
            if (newBp is not null)
            {
                newBp.Condition = condition;
                newBp.HitCondition = hitCondition;
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
            State.Output.Content += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(data)}</div>";
        else
            State.Output.Content += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
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
                    State.Output.Content = State.Output.Content.Replace(previous, wrapped);
                else
                    State.Output.Content += wrapped;
                State.DumpContainerBlocks[id] = wrapped;
            }
            else
            {
                State.Output.Content += wrapped;
            }
        }
        catch
        {
            State.Output.Content += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(base64)}</div>";
        }

        UpdateOutputUi();
    }

    private void AppendError(Exception ex)
    {
        State.HadRunError = true;
        State.Output.Content += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>";
        if (ex.StackTrace is { } st)
            State.Output.Content += $"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>";
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
        => Dispatcher.UIThread.Post(() => ApplyEditorSettings(State.Editor, _prefs.Editor));

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

    private static void ApplyEditorSettings(CodeTextEditor editor, EditorSettings s)
    {
        editor.ShowLineNumberText = s.ShowLineNumbers;
        editor.ShowFolding = s.ShowFolding;
        editor.FontSize = s.FontSize;
        editor.WordWrap = s.WordWrap;
        editor.Options.ShowTabs = s.ShowTabs;
        editor.Options.ShowSpaces = s.ShowSpaces;
        editor.Options.ShowEndOfLine = s.ShowEndOfLine;
        editor.Options.ColumnRulerPositions = s.ShowColumnRuler
            ? [s.ColumnRulerPosition]
            : [];
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
                    // Item 60 point 6 / TODO #59 remainder: the list row itself gets a dimmed
                    // "(in Namespace)"/"(+N overloads)" annotation for a not-yet-imported type or a
                    // name collapsing several overloads; both null/1 for everything else, so
                    // BuildListRow just wraps the plain name (same TextBlock AvaloniaEdit's own
                    // default template would've produced for a bare string Content anyway).
                    content: DisplaySegmentRenderer.BuildListRow(item.DisplayText, item.Detail, item.OverloadCount)
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

        // ── Code fixes (lightbulb: real Roslyn CodeFixProviders) ────────────────
        intelliSense.CodeFixesChanged += (code, fixes) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;

                // Usings go to the .rpad front matter's `namespaces:` list (a project file, per
                // docs/ImplementationPlanRPad.md), not the visible script — except for a plain
                // script with no front matter to route them to, where they're folded into the text.
                var hasProjectFile = State.RpadDocument is not null;
                var entries = fixes.Select(f => hasProjectFile
                        ? new CodeFixEntry(f.Line, f.Title, f.NewCode, f.UsingsToAdd)
                        : new CodeFixEntry(f.Line, f.Title, PrependUsings(f.UsingsToAdd, f.NewCode), []))
                    .ToList();
                editor.UpdateCodeFixes(entries);
            });

        editor.UsingsToAddRequested += AddGlobalUsings;

        editor.TextChanged += (_, _) => intelliSense.NotifyTextChanged(editor.Text);

        // ── Quick Info tooltips on hover ──────────────────────────────────────
        ToolTip.SetShowDelay(editor.TextArea.TextView, 0);
        var quickInfoCts = new CancellationTokenSource();

        editor.TextArea.TextView.PointerMoved += (_, e) =>
        {
            quickInfoCts.Cancel();
            quickInfoCts.Dispose();
            quickInfoCts = new CancellationTokenSource();
            var ct = quickInfoCts.Token;
            var pos = e.GetPosition(editor);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, ct);
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
            content = ResourceLoader.LoadSampleFile(string.Empty);
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
        editor.Document = new TextDocument(content);

        // Shows which kind/target framework the script actually runs as (ANALYSIS.md #29) — helps
        // tell the two compile paths apart (fast path vs. DotnetProcessCompiler) and diagnose
        // compile/run discrepancies between them without having to open the front matter by hand.
        //ProjectInfoBlock.Text = rpad is null ? "" : $"{rpad.FrontMatter.Kind} · {rpad.FrontMatter.TargetSdk}";

        RefreshIntelliSenseKind();
        if (rpad?.FrontMatter.Packages is { Count: > 0 })
            RefreshIntelliSensePackages();
        if (rpad?.FrontMatter.Namespaces is { Count: > 0 })
            RefreshIntelliSenseUsings();
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
        _intelliSense?.NotifyTextChanged(State.Editor.Text);
        SaveIfNeeded();
    }

    private static string PrependUsings(IReadOnlyList<string> usings, string code) =>
        usings.Count == 0 ? code : string.Concat(usings.Select(u => $"using {u};\n")) + code;

    private static string LangToExtension(string? language) => language?.ToLowerInvariant() switch
    {
        "fsharp" => ".fs",
        "vb" => ".vb",
        _ => ".cs",
    };

    // The id ILanguageHostRegistry.Create/ILanguageHostFactory.Language use — matches
    // RpadLanguage's own lowercased member name (RpadLanguage.CSharp → "csharp", RpadLanguage.Vb →
    // "vb") by construction, so no separate mapping table is needed. Defaults to C# for untitled
    // tabs (no RpadDocument yet — see SetDocumentContent).
    private string ResolveLanguageId()
        => (State.RpadDocument?.FrontMatter.Language ?? RpadLanguage.CSharp).ToString().ToLowerInvariant();

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
        var assemblyReferences = rpad?.FrontMatter.References?
            .Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var projectReferences = rpad?.FrontMatter.References?
            .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (ResolveLanguageId() == "vb")
        {
            return new VbLanguageHostOptions
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
            };
        }

        return new CSharpLanguageHostOptions
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
        };
    }

    private sealed class ScriptBreakpoint(string fileName, int line, string? condition, string? hitCondition = null)
        : Contracts.IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition { get; } = condition;
        public string? HitCondition { get; } = hitCondition;
    }
}
