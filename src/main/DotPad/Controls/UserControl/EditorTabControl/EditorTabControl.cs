using System.Text;
using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Models;
using DotPad.Resources;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.ViewModels;

namespace DotPad.Views;

public partial class EditorTabControl : UserControl, IDisposable
{
    private readonly ILanguageHostRegistry _registry;
    private readonly IPreferencesService   _prefs;
    private readonly EditorTabViewModel    _vm;

    internal readonly EditorTabState State;

    internal EditorTabViewModel Vm => _vm;

    internal event Action<EditorTabControl>? StateChanged;

    internal EditorTabControl(
        EditorTabViewModel    vm,
        IThemeService         themeService,
        ILanguageHostRegistry registry,
        IPreferencesService   prefs,
        IIntelliSenseService? intelliSense,
        IBrush?               editorBackground)
    {
        _registry   = registry;
        _prefs      = prefs;
        _vm         = vm;
        DataContext = _vm;

        InitializeComponent();

        var editor      = new CodeTextEditor { Background = editorBackground };
        State           = new EditorTabState(editor);
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

        EditorHost.Content = editor;
    }

    // ── Public API for MainWindow ─────────────────────────────────────────────

    internal TabItem AddTo(TabControl tabControl)
    {
        Resources.TryGetResource("TabHeaderTemplate", null, out var tmpl);
        var tab = new TabItem
        {
            Header         = _vm,
            HeaderTemplate = (IDataTemplate)tmpl!,
            Content        = this,
        };
        tabControl.Items.Add(tab);
        return tab;
    }

    internal void ApplyTheme(ActiveTheme theme, IBrush? editorBg)
    {
        State.Editor.SetTheme(theme.Editor.TextMateName);
        if (editorBg is not null) State.Editor.Background = editorBg;
        ApplySelectionColor(State.Editor);
        State.OutputCss = HtmlThemeStylesheet.Generate(theme.Interface.Variant);
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

        _vm.EnableRunButton    = false;
        _vm.EnableCancelButton = true;
        _vm.IsDebugging        = true;
        _vm.IsPaused           = false;
        _vm.IsRunningProgress  = true;
        s.OutputContent        = "";
        s.HadRunError          = false;
        s.ElapsedText          = "";
        _vm.DebugInfo          = "Debugging…";
        SetStatus("Process", "DotPad.Status.RunningBrush");
        s.RunStopwatch.Restart();
        NotifyChanged();

        s.RunCts = new CancellationTokenSource();

        var workDir     = EnsureWorkDirectory();
        var options     = BuildRunOptions(platform, workDir);
        var script      = s.Editor.Text;
        var breakpoints = s.Editor.Breakpoints
            .Select(bp => (DotPad.Contracts.IBreakpoint)new ScriptBreakpoint(bp.Line))
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
        catch (Exception ex)               { AppendError(ex); }
        finally
        {
            s.RunningHost = null;
            await s.RunCts.CancelAsync();

            _vm.EnableRunButton    = true;
            _vm.EnableCancelButton = false;
            _vm.IsDebugging        = false;
            _vm.IsPaused           = false;
            _vm.IsRunningProgress  = false;
            if (debug) _vm.DebugInfo = "Debug session ended.";
            s.RunStopwatch.Stop();
            s.ElapsedText = FormatElapsed(s.RunStopwatch.Elapsed);

            if (cancelled)           SetStatus("Cancelled", "DotPad.Status.CancelledBrush");
            else if (!s.HadRunError) SetStatus("Success",   "DotPad.Status.SuccessBrush");

            NotifyChanged();
        }
    }

    internal void ResumeDebug(DebugAction action)
    {
        _vm.IsPaused          = false;
        _vm.IsRunningProgress = true;
        _vm.DebugInfo         = $"Running… (last action: {action})";
        SetStatus("Process", "#4FC1FF");
        var ctx = State.CurrentDebugCtx;
        State.CurrentDebugCtx = null;
        ctx?.Resume(action);
        NotifyChanged();
    }

    internal async Task PauseAsync()
    {
        if (State.RunningHost is not null)
            await State.RunningHost.PauseAsync();
    }

    public void Dispose()
    {
        _prefs.Changed -= OnPreferencesChanged;
        Cancel();
        State.RunCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Toolbar click handlers ────────────────────────────────────────────────

    private async void RunButton_Click(object? sender, RoutedEventArgs e)
        => await RunAsync(_vm.DebugModeEnabled, _vm.SelectedDotnetPlatform ?? _vm.DotnetPlatforms.First());

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Cancel();

    // ── Private ───────────────────────────────────────────────────────────────

    private void NotifyChanged() => StateChanged?.Invoke(this);

    private void SetStatus(string text, string hex)
    {
        State.ExecStatusText  = text;
        State.ExecStatusColor = hex;
    }

    private void AppendOutput(string data)
    {
        State.OutputContent += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
        NotifyChanged();
    }

    private void AppendError(Exception ex)
    {
        State.HadRunError    = true;
        State.OutputContent += $"\n<div class=\"error\">{HttpUtility.HtmlEncode(ex.Message)}</div>";
        if (ex.StackTrace is { } st)
            State.OutputContent += $"\n<pre class=\"stacktrace\">{HttpUtility.HtmlEncode(st)}</pre>";
        SetStatus("Error", "DotPad.Status.ErrorBrush");
        NotifyChanged();
    }

    private void OnBreakpointHit(IBreakpointHitContext context)
    {
        var s = State;
        s.CurrentDebugCtx    = context;
        _vm.IsPaused         = true;
        _vm.IsRunningProgress = false;

        var sb = new StringBuilder();
        sb.AppendLine($"⏸ Paused at line {context.Breakpoint.Line}  (thread {context.ThreadId})");
        if (context.Threads.Count > 0)
        {
            sb.AppendLine().AppendLine("Threads:");
            foreach (var t in context.Threads) sb.AppendLine($"  [{t.Id}] {t.Name}");
        }
        if (context.LocalVariables.Count > 0)
        {
            sb.AppendLine().AppendLine("Locals:");
            foreach (var v in context.LocalVariables)
                sb.AppendLine($"  {v.TypeName} {v.Name} = {v.Value}");
        }
        if (context.CallStack.Count > 0)
        {
            sb.AppendLine().AppendLine("Call stack:");
            foreach (var f in context.CallStack)
                sb.AppendLine($"  {f.MethodName}  ({Path.GetFileName(f.FileName)}:{f.Line})");
        }

        _vm.DebugInfo = sb.ToString();
        SetStatus("Paused", "DotPad.Status.PausedBrush");
        NotifyChanged();
    }

    // ── Editor preferences ────────────────────────────────────────────────────

    private void OnPreferencesChanged()
        => Dispatcher.UIThread.Post(() => ApplyEditorSettings(State.Editor, _prefs.Editor));

    private static void ApplySelectionColor(CodeTextEditor editor)
    {
        // TextMate may set SelectionBrush to a dark color even for light themes,
        // and SelectionForeground to white — both unreadable on a light background.
        // Read the brush from the current theme so it's fully configurable per theme.
        var variant = Application.Current?.ActualThemeVariant;
        if (Application.Current?.TryGetResource("DotPad.Editor.SelectionBrush", variant, out var res) == true
            && res is IBrush brush)
            editor.TextArea.SelectionBrush = brush;
        editor.TextArea.SelectionForeground = null; // inherit from syntax highlighting
    }

    private static void ApplyEditorSettings(CodeTextEditor editor, EditorSettings s)
    {
        editor.ShowLineNumbers             = s.ShowLineNumbers;
        editor.FontSize                    = s.FontSize;
        editor.WordWrap                    = s.WordWrap;
        editor.Options.ShowTabs            = s.ShowTabs;
        editor.Options.ShowSpaces          = s.ShowSpaces;
        editor.Options.ShowEndOfLine       = s.ShowEndOfLine;
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
        _                     => $"{(int)t.TotalMinutes}m {t.Seconds}s",
    };

    private static string EnsureWorkDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotPad");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private CSharpLanguageHostOptions BuildRunOptions(ExecutionPlatform platform, string workDir) => new()
    {
        Platform         = platform,
        ExecutionOptions = new ExecutionOptions
        {
            WorkingDirectory = workDir,
            Runtime  = new RuntimeOptions  { RuntimePath  = _prefs.Debugger.DotnetPath   },
            Debugger = new DebuggerOptions { DebuggerPath = _prefs.Debugger.DebuggerPath },
        },
        OutputDirectory = Path.Combine(workDir, Path.GetRandomFileName()),
        RuntimeLibPath  = new CSharpReferenceService().GetExtensionReference(),
    };

    private sealed class ScriptBreakpoint(int line) : DotPad.Contracts.IBreakpoint
    {
        public string FileName { get; } = string.Empty;
        public int    Line     { get; set; } = line;
    }
}
