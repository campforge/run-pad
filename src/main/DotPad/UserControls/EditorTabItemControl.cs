using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using DotPad.Assets.Models;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
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

    internal EditorsTabItemControl(
        EditorTabItemViewModel vm,
        IThemeService themeService,
        ILanguageHostRegistry registry,
        IPreferencesService prefs,
        IIntelliSenseService? intelliSense,
        IBrush? editorBackground)
    {
        _themeService = themeService;
        _registry = registry;
        _prefs = prefs;
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
        State.OutputCss = HtmlThemeStylesheet.Generate(themeService.CurrentTheme.Interface.Variant);

        ApplyEditorSettings(editor, prefs.Editor);
        prefs.Changed += OnPreferencesChanged;

        SetupEditor(editor, intelliSense);

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
            editor.SetLanguage(themeName, ".cs");
            ApplySelectionColor(editor);
        };
        editor.AttachedToVisualTree += onAttached;

        // Caret position → StatusLine
        editor.TextArea.Caret.PositionChanged += (_, _) =>
            StatusLine.Text = $"Line {editor.TextArea.Caret.Line}  Col {editor.TextArea.Caret.Column}";

        EditorHost.Content = editor;
    }

    internal void ApplyTheme(ActiveTheme theme)
    {
        State.Editor.SetTheme(theme.Editor.TextMateName);
        var editorBg = _themeService!.GetBrush("DotPad.Editor.BackgroundBrush");

        if (editorBg is not null)
        {
            State.Editor.Background = editorBg;
        }

        ApplySelectionColor(State.Editor);
        State.OutputCss = HtmlThemeStylesheet.Generate(theme.Interface.Variant);
        UpdateOutputUi();
    }

    internal void SaveIfNeeded()
    {
        if (_vm.FilePath is null) return;
        try { File.WriteAllText(_vm.FilePath, State.Editor.Text); } catch { /* best-effort */ }
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

        SaveIfNeeded();

        _vm.RunState = debug ? RunState.RunDebug : RunState.Run;
        s.OutputContent = "";
        s.HadRunError = false;
        s.ElapsedText = "";
        SetStatus("Process", "DotPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        UpdateOutputUi();

        s.RunCts = new CancellationTokenSource();

        var workDir = EnsureWorkDirectory();
        var options = BuildRunOptions(platform, workDir);
        var script = s.Editor.Text;
        var breakpoints = s.Editor.Breakpoints
            .Select(bp => (Contracts.IBreakpoint)new ScriptBreakpoint(bp.Line))
            .ToList();

        var cancelled = false;
        try
        {
            await Task.Run(async () =>
            {
                await using var host = _registry.Create("csharp", options);
                s.RunningHost = host;

                if (debug && host.Supports(LanguageHostCapabilities.Debug))
                {
                    foreach (var bp in breakpoints) host.AddBreakpoint(bp);
                    host.BreakpointHit += ctx =>
                        Dispatcher.UIThread.Post(() => OnBreakpointHit(ctx));
                    await host.RunDebugAsync(
                        script,
                        (data, _) => Dispatcher.UIThread.Post(() => AppendOutput(data)),
                        s.RunCts.Token);
                }
                else
                {
                    await host.RunAsync(
                        script,
                        (data, _) => Dispatcher.UIThread.Post(() => AppendOutput(data)),
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
        _prefs.Changed -= OnPreferencesChanged;
        Cancel();
        State.RunCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
        => await RunAsync(_vm.DebugModeEnabled, _vm.SelectedDotnetPlatform ?? _vm.DotnetPlatforms.First());

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Cancel();

    // ── Debug panel handlers ──────────────────────────────────────────────────

    private void DbgContinue_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.Continue);
    private void DbgStepOver_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepOver);
    private void DbgStepIn_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepIn);
    private void DbgStepOut_Click(object? sender, RoutedEventArgs e) => ResumeDebug(DebugAction.StepOut);

    private async void DbgPause_Click(object? sender, RoutedEventArgs e)
    {
        if (State.RunningHost is not null)
            await State.RunningHost.PauseAsync();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void ResumeDebug(DebugAction action)
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

    private void AppendOutput(string data)
    {
        State.OutputContent += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
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

    private void OnBreakpointHit(IBreakpointHitContext context)
    {
        var s = State;
        s.CurrentDebugCtx = context;
        _vm.RunState      = RunState.Paused;
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
            _vm.DebugLocals.Add(new DebugLocal(v.Name, v.Value, v.TypeName));

        _vm.DebugCallStack.Clear();
        foreach (var f in context.CallStack)
            _vm.DebugCallStack.Add(new DebugFrame(f.MethodName, Path.GetFileName(f.FileName), f.Line));

        SetStatus("Paused", "DotPad.Status.PausedBrush");
        UpdateOutputUi();
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
        editor.ShowLineNumbers = s.ShowLineNumbers;
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
        editor.TextArea.IndentationStrategy =
            new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(editor.Options);

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
    }

    private static void SetDocumentContent(CodeTextEditor editor, string? filePath)
    {
        var content = filePath is null
            ? ResourceLoader.LoadSampleFile(string.Empty)
            : (File.Exists(filePath) ? File.ReadAllText(filePath) : "");
        editor.Document = new TextDocument(content);
    }

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

    private CSharpLanguageHostOptions BuildRunOptions(ExecutionPlatform platform, string workDir) => new()
    {
        Platform = platform,
        ExecutionOptions = new ExecutionOptions
        {
            WorkingDirectory = workDir,
            Runtime = new RuntimeOptions { RuntimePath = _prefs.Debugger.DotnetPath },
            Debugger = new DebuggerOptions { DebuggerPath = _prefs.Debugger.DebuggerPath },
        },
        OutputDirectory = Path.Combine(workDir, Path.GetRandomFileName()),
        RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
    };

    private sealed class ScriptBreakpoint(int line) : Contracts.IBreakpoint
    {
        public string FileName { get; } = string.Empty;
        public int Line { get; set; } = line;
    }
}
