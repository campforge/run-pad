using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DotPad.Assets.Models;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Resources;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.UserControls.Models;

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
    private bool _disposed;

    internal EditorsTabItemControl(
        EditorTabItemViewModel vm,
        IThemeService themeService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IIntelliSenseService? intelliSense,
        CSharpReferenceService referenceService,
        IBrush? editorBackground)
    {
        _themeService = themeService;
        _registry = registry;
        _prefs = prefs;
        _referenceService = referenceService;
        _vm = vm;
        DataContext = _vm;

        InitializeComponent();

        this.FindControl<DebugPanel>("DebugPanelCtrl")!.DataContext = _vm;
        _outputTabsControl = this.FindControl<TabControl>("OutputTabsControl");

        // Register custom font for HTML output panel
        OutputHtml.Container.AddFontFamily(
            new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));

        var editor = new CodeTextEditor { Background = editorBackground };
        State = new EditorTabState(editor);
        State.OutputCss = HtmlThemeStylesheet.Generate(themeService.CurrentTheme.Interface.Variant, themeService.CurrentTheme.Interface.IsDark);

        ApplyEditorSettings(editor, prefs.Editor);
        prefs.Changed += OnPreferencesChanged;

        _intelliSense = intelliSense;
        SetupEditor(editor, intelliSense);
        editor.BreakpointAdded += OnGutterBreakpointAdded;
        editor.BreakpointRemoved += OnGutterBreakpointRemoved;

        // Load document content first, without TextMate.
        // SetLanguage calls Redraw() internally, which crashes when the editor
        // is not yet in the visual tree (no visual lines exist yet).
        // We defer SetLanguage to AttachedToVisualTree so Redraw() works on the
        // first call and highlighting appears immediately, without NullReferenceException.
        SetDocumentContent(editor, _vm.FilePath);

        var themeName = themeService.CurrentTheme.Editor.TextMateName;
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (s, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            var langExt = LangToExtension(State.RpadDocument?.FrontMatter.Language);
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
        _vm.WatchExpressions.CollectionChanged += (_, e) =>
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
        if (string.IsNullOrWhiteSpace(w.Expression)) { w.Value = "?"; w.TypeName = ""; return; }
        if (State.CurrentDebugCtx is not { } ctx) { w.Value = "<not paused>"; w.TypeName = ""; return; }

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
        State.OutputCss = HtmlThemeStylesheet.Generate(theme.Interface.Variant, theme.Interface.IsDark);
        UpdateOutputUi();
    }

    internal void SaveIfNeeded()
    {
        if (_vm.FilePath is null) return;
        try { WriteFile(_vm.FilePath); } catch { /* best-effort */ }
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

    internal async Task RunAsync(bool debug, ExecutionPlatform platform)
    {
        var s = State;
        if (s.RunCts is { IsCancellationRequested: false }) return;

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.OutputContent = "";
        s.DumpContainerBlocks.Clear();
        s.HadRunError = false;
        s.ElapsedText = "";
        SetStatus("Process", "DotPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        UpdateOutputUi();

        if (_vm.FilePath is not null)
        {
            try { WriteFile(_vm.FilePath); }
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
            // Matches the path DotnetProcessCompiler writes the source to and CSharpDapSession expects
            // it at (OutputDirectory/{OutputName}.cs) — see ScriptBreakpoint below.
            var sourceFilePath = Path.Combine(options.OutputDirectory, $"{options.OutputName}.cs");
            s.SourceFilePath = sourceFilePath;
            var breakpoints = s.Editor.Breakpoints
                .Select(bp => (Contracts.IBreakpoint)new ScriptBreakpoint(sourceFilePath, bp.Line, bp.Condition))
                .ToList();

            // Reset to Pending — stale Verified/Unverified color from a previous run shouldn't linger
            // in the margin until the new session's setBreakpoints response arrives.
            foreach (var bp in s.Editor.Breakpoints)
                bp.VerificationState = DotPad.Avalonia.CodeTextEditor.BreakpointVerificationState.Pending;
            s.Editor.InvalidateBreakpoints();

            await Task.Run(async () =>
            {
                await using var host = _registry.Create("csharp", options);
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
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { AppendError(ex); }
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
        Cancel();
        State.RunCts?.Dispose();
        _intelliSense?.Dispose(); // owned by this tab now — see EditorTabsControl.CreateControl
        GC.SuppressFinalize(this);
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
    {
        var platform = _vm.SelectedDotnetPlatform ?? _vm.DotnetPlatforms.FirstOrDefault();
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
        if (State.RunningHost is not null)
            await State.RunningHost.PauseAsync();
    }

    private async void DbgRunToCursor_Click(object? sender, RoutedEventArgs e)
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

    private async void OnBreakpointRightClicked(int line, DotPad.Avalonia.CodeTextEditor.IBreakpoint? bp)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new DotPad.Windows.ConditionDialog(bp?.Condition);
        var result = await dialog.ShowDialog<string?>(owner);
        if (result is null) return; // cancelled

        var condition = string.IsNullOrWhiteSpace(result) ? null : result;

        if (bp is not null)
        {
            bp.Condition = condition;
        }
        else if (condition is not null)
        {
            State.Editor.AddBreakpoint(line);
            var newBp = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == line);
            if (newBp is not null) newBp.Condition = condition;
        }
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
        OutputHtml.Text = $"""
            <html>
            <head><style>{State.OutputCss}</style></head>
            <body>{State.OutputContent}</body>
            </html>
            """;

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
        if (data.Length > 0 && data[0] == '\x1e')
        {
            RenderDump(data[1..]);
            return;
        }
        if (data.StartsWith("DOTPAD_DUMP:", StringComparison.Ordinal))
        {
            RenderDump(data["DOTPAD_DUMP:".Length..]);
            return;
        }

        if (kind == OutputKind.StandardError)
            State.OutputContent += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(data)}</div>";
        else
            State.OutputContent += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
        UpdateOutputUi();
    }

    private static readonly System.Text.RegularExpressions.Regex DumpContainerIdRegex =
        new("data-dp-id=\"([a-f0-9]+)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void RenderDump(string base64)
    {
        try
        {
            var html = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            var wrapped = $"\n<div class=\"dp-dump\">{html}</div>";

            // DumpContainer (ANALYSIS.md feature #19) re-emits the same data-dp-id on every
            // update — replace its existing block in place instead of appending a new one.
            var idMatch = DumpContainerIdRegex.Match(html);
            if (idMatch.Success)
            {
                var id = idMatch.Groups[1].Value;
                if (State.DumpContainerBlocks.TryGetValue(id, out var previous))
                    State.OutputContent = State.OutputContent.Replace(previous, wrapped);
                else
                    State.OutputContent += wrapped;
                State.DumpContainerBlocks[id] = wrapped;
            }
            else
            {
                State.OutputContent += wrapped;
            }
        }
        catch
        {
            State.OutputContent += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(base64)}</div>";
        }
        UpdateOutputUi();
    }

    private void AppendError(Exception ex)
    {
        State.HadRunError = true;
        State.OutputContent += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>";
        if (ex.StackTrace is { } st)
            State.OutputContent += $"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>";
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
            ? DotPad.Avalonia.CodeTextEditor.BreakpointVerificationState.Verified
            : DotPad.Avalonia.CodeTextEditor.BreakpointVerificationState.Unverified;
        editorBp.VerificationMessage = message;
        State.Editor.InvalidateBreakpoints();
    }

    // Forwards gutter breakpoint edits to the running debug session, if there is one — otherwise
    // they only take effect the next time RunAsync reads State.Editor.Breakpoints at run start.
    private void OnGutterBreakpointAdded(object? sender, BreakpointEventArgs e)
    {
        if (State.RunningHost is not { } host || !host.Supports(LanguageHostCapabilities.Breakpoints)) return;
        if (State.SourceFilePath is not { } path) return;
        var condition = State.Editor.Breakpoints.FirstOrDefault(b => b.Line == e.Line)?.Condition;
        host.AddBreakpoint(new ScriptBreakpoint(path, e.Line, condition));
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

        // Switch to Debugger tab so the user sees variables immediately
        if (_outputTabsControl is { } tc) tc.SelectedIndex = 1;

        _vm.DebugThreads.Clear();
        foreach (var t in context.Threads)
            _vm.DebugThreads.Add(new DebugThread(t.Id, t.Name));

        _vm.DebugLocals.Clear();
        foreach (var v in context.LocalVariables)
            _vm.DebugLocals.Add(CreateDebugLocal(v, context));

        _vm.DebugCallStack.Clear();
        foreach (var f in context.CallStack)
            _vm.DebugCallStack.Add(new DebugFrame(f.MethodName, Path.GetFileName(f.FileName), f.Line));

        // Watch expressions are evaluated against whichever frame/scope is current, so refresh them
        // for the new stop — stale values from the previous breakpoint hit would be misleading.
        foreach (var w in _vm.WatchExpressions) _ = EvaluateWatchAsync(w);

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
        editor.CompletionTriggers.Add('.');
        editor.OverloadTriggers.Add('(');
        editor.OverloadTriggers.Add(',');
        editor.TextArea.IndentationStrategy =
            new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(editor.Options);

        editor.BreakpointRightClicked += OnBreakpointRightClicked;

        editor.Snippets["for"] = "for (int i = 0; i < ${0}; i++)\n{\n\t\n}";
        editor.Snippets["foreach"] = "foreach (var item in ${0})\n{\n\t\n}";
        editor.Snippets["while"] = "while (${0})\n{\n\t\n}";
        editor.Snippets["do"] = "do\n{\n\t${0}\n} while ();";
        editor.Snippets["if"] = "if (${0})\n{\n\t\n}";
        editor.Snippets["ife"] = "if (${0})\n{\n\t\n}\nelse\n{\n\t\n}";
        editor.Snippets["try"] = "try\n{\n\t${0}\n}\ncatch (Exception ex)\n{\n\t\n}";
        editor.Snippets["tryf"] = "try\n{\n\t${0}\n}\nfinally\n{\n\t\n}";
        editor.Snippets["sw"] = "switch (${0})\n{\n\tcase :\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}";
        editor.Snippets["prop"] = "public ${0} Property { get; set; }";
        editor.Snippets["propg"] = "public ${0} Property { get; private set; }";
        editor.Snippets["class"] = "class ${0}\n{\n\t\n}";
        editor.Snippets["interface"] = "interface I${0}\n{\n\t\n}";
        editor.Snippets["ctor"] = "public ${0}()\n{\n\t\n}";
        editor.Snippets["using"] = "using (var resource = ${0})\n{\n\t\n}";
        editor.Snippets["lock"] = "lock (${0})\n{\n\t\n}";

        if (intelliSense is null) return;

        editor.AsyncCompletionProvider = async e =>
        {
            var list = await intelliSense.GetCompletionsAsync(
                State.Editor.Text, e.CaretOffset, CompletionTrigger.Insertion(e.Trigger));
            if (list is null) return;
            foreach (var item in list.Items) e.Items.Add(new EditorCompletionData(item));
        };

        editor.AsyncOverloadProvider = async e =>
        {
            var result = await intelliSense.GetSignatureHelpAsync(
                State.Editor.Text, e.CaretOffset, e.Trigger);
            if (result is null) return;
            foreach (var item in result.Items)
                e.Overloads.Add((item.DisplaySignature, item.Documentation));
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

        // ── Code fixes (lightbulb: "add using") ────────────────────────────────
        intelliSense.CodeFixesChanged += (code, fixes) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (!ReferenceEquals(editor.Text, code) && editor.Text != code) return;
                editor.UpdateCodeFixes(fixes.Select(f => new CodeFixEntry(f.Line, f.Title, f.NewCode)).ToList());
            });

        editor.TextChanged += (_, _) => intelliSense.NotifyTextChanged(editor.Text);

        // ── Quick Info tooltips on hover ──────────────────────────────────────
        ToolTip.SetShowDelay(editor.TextArea.TextView, 0);
        var quickInfoCts = new CancellationTokenSource();

        editor.TextArea.TextView.PointerMoved += (_, e) =>
        {
            quickInfoCts.Cancel();
            quickInfoCts = new CancellationTokenSource();
            var ct = quickInfoCts.Token;
            var pos = e.GetPosition(editor);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, ct);
                    var offset = await Dispatcher.UIThread.InvokeAsync(
                        () => editor.GetOffsetFromPoint(pos));
                    if (offset < 0 || ct.IsCancellationRequested) return;

                    // A diagnostic under the cursor takes priority over Quick Info — seeing why a
                    // squiggle is there is usually more useful than the symbol's signature.
                    var diagnostic = squiggleRenderer.Diagnostics.FirstOrDefault(
                        d => offset >= d.StartOffset && offset < d.EndOffset);
                    var content = diagnostic?.Message
                        ?? (await intelliSense.GetQuickInfoAsync(editor.Text, offset, ct))?.Content;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            ToolTip.SetTip(editor.TextArea.TextView, content);
                    });
                }
                catch (OperationCanceledException) { }
            });
        };

        editor.TextArea.TextView.PointerExited += (_, _) =>
        {
            quickInfoCts.Cancel();
            ToolTip.SetTip(editor.TextArea.TextView, null);
        };
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
        }
        else
        {
            content = "";
        }

        State.RpadDocument = rpad;
        _vm.FrontMatter = rpad?.FrontMatter;
        editor.Document = new TextDocument(content);

        if (rpad?.FrontMatter.Packages is { Count: > 0 })
            RefreshIntelliSensePackages();
    }

    /// <summary>
    /// Pushes this tab's current NuGet packages into its own IntelliSense instance. Called on initial
    /// load and again after the Dependencies window closes, since it mutates the same RpadFrontMatter
    /// in place without any change notification of its own.
    /// </summary>
    internal void RefreshIntelliSensePackages()
        => _intelliSense?.UpdatePackageReferences(State.RpadDocument?.FrontMatter.Packages ?? new Dictionary<string, string>());

    private static string LangToExtension(string? language) => language?.ToLowerInvariant() switch
    {
        "fsharp" => ".fs",
        "vb" => ".vb",
        _ => ".cs",
    };

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

    private CSharpLanguageHostOptions BuildRunOptions(ExecutionPlatform platform, string workDir)
    {
        var rpad = State.RpadDocument;

        // Reused across runs of this tab (allocated once) so DotnetProcessCompiler can detect an
        // unchanged .csproj and skip NuGet restore — see its Compilation() for the actual check.
        State.OutputDirectory ??= Path.Combine(workDir, Path.GetRandomFileName());

        return new CSharpLanguageHostOptions
        {
            Platform = platform,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = workDir,
                Runtime = new RuntimeOptions { RuntimePath = _prefs.Debugger.DotnetPath },
                Debugger = new DebuggerOptions { DebuggerPath = _prefs.Debugger.DebuggerPath },
            },
            OutputDirectory = State.OutputDirectory,
            RuntimeLibPath = _referenceService.GetExtensionReference(),
            BreakOnAllExceptions = _vm.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = _vm.BreakOnUncaughtExceptions,
            JustMyCode = _vm.JustMyCode,
            Kind = rpad?.FrontMatter.Kind ?? "Statements",
            TargetFrameworkOverride = rpad?.FrontMatter.TargetFramework,
            NugetPackages = rpad?.FrontMatter.Packages ?? new Dictionary<string, string>(),
            GlobalUsings = rpad?.FrontMatter.Usings ?? [],
            AssemblyReferences = rpad?.FrontMatter.References?
                .Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [],
            ProjectReferences = rpad?.FrontMatter.References?
                .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [],
        };
    }

    private sealed class ScriptBreakpoint(string fileName, int line, string? condition) : Contracts.IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition { get; } = condition;
    }
}
