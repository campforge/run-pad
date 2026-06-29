using System.Web;
using Avalonia.Controls;
using Avalonia.HtmlRenderer;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Languages.CSharp;
using DotPad.Models;
using DotPad.Resources;
using DotPad.Services;
using DotPad.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DotPad.Views;

public partial class MainWindow : Window, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly ILanguageHostRegistry _registry;
    private readonly IOptionsService _optionsService;
    private readonly IServiceProvider _serviceProvider;

    private CodeTextEditor? _textEditor;
    private HtmlPanel? _htmlPanel;
    private TextBlock? _statusText;
    private TabControl? _outputTabs;
    private CancellationTokenSource? _runCts;

    // Current breakpoint hit context — non-null while the debugger is paused.
    private IBreakpointHitContext? _currentDebugContext;

    // The running language host, held so PauseAsync can reach it.
    private ILanguageHost? _runningHost;

    public MainWindow(
        IThemeService themeService,
        ILanguageHostRegistry registry,
        IOptionsService optionsService,
        IServiceProvider serviceProvider)
    {
        _themeService    = themeService;
        _registry        = registry;
        _optionsService  = optionsService;
        _serviceProvider = serviceProvider;

        InitializeComponent();

        _themeService.ThemeChange += OnThemeChanged;
        InitializeHtmlPanel();
        InitializeCodeTextEditor();
    }

    private void InitializeHtmlPanel()
    {
        _htmlPanel = this.FindControl<HtmlPanel>("HtmlPanel");
        if (_htmlPanel is null) return;

        _htmlPanel.Container.AddFontFamily(
            new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));
        _htmlPanel.Text = "\n<div>Init</div>";
    }

    private void InitializeCodeTextEditor()
    {
        _textEditor = this.FindControl<CodeTextEditor>("Editor");
        _statusText = this.Find<TextBlock>("StatusText");
        _outputTabs = this.FindControl<TabControl>("OutputTabs");
        if (_textEditor is null) return;

        _textEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        _textEditor.BreakpointAdded   += (_, e) => Console.WriteLine($"BP added at line {e.Line}");
        _textEditor.BreakpointRemoved += (_, e) => Console.WriteLine($"BP removed at line {e.Line}");

        _textEditor.TextArea.IndentationStrategy =
            new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(_textEditor.Options);

        var scopeName = _textEditor.SetLanguage(_themeService.CurrentTheme.CodeEditorThemeName, ".cs");
        _textEditor.Document = new TextDocument(ResourceLoader.LoadSampleFile(scopeName));
    }

    private void OnThemeChanged(Theme theme) => _textEditor?.SetTheme(theme.CodeEditorThemeName);

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_textEditor is null || _statusText is null) return;
        _statusText.Text = $"Line {_textEditor.TextArea.Caret.Line} Column {_textEditor.TextArea.Caret.Column}";
    }

    // ── Run / Cancel ──────────────────────────────────────────────────────────

    private async void RunButton_Click(object sender, RoutedEventArgs eventsArgs)
    {
        try
        {
            if (_textEditor is null) return;
            if (_runCts is { IsCancellationRequested: false }) return;

            var model = GetViewModel();
            model.EnableRunButton    = false;
            model.EnableCancelButton = true;
            model.IsDebugging        = false;
            model.IsPaused           = false;
            _htmlPanel?.Text         = string.Empty;

            _runCts = new CancellationTokenSource();

            var workDir = EnsureWorkDirectory();
            var options = BuildRunOptions(model, workDir);
            var script  = _textEditor.Text;
            var debug   = model.DebugModeEnabled;

            var breakpoints = _textEditor.Breakpoints
                .Select(bp => (DotPad.Contracts.IBreakpoint)new ScriptBreakpoint(bp.Line))
                .ToList();

            model.DebugInfo   = "Debugging…";
            model.IsDebugging = true;

            try
            {
                await Task.Run(async () =>
                {
                    await using var host = _registry.Create("csharp", options);
                    _runningHost = host;

                    if (debug && host.Supports(LanguageHostCapabilities.Debug))
                    {
                        foreach (var bp in breakpoints)
                            host.AddBreakpoint(bp);

                        host.BreakpointHit += ctx =>
                            Dispatcher.UIThread.Post(() => OnBreakpointHit(ctx));

                        await host.RunDebugAsync(
                            script,
                            (data, _) => Dispatcher.UIThread.Post(() => AppendOutput(data)),
                            _runCts.Token);
                    }
                    else
                    {
                        await host.RunAsync(
                            script,
                            (data, _) => Dispatcher.UIThread.Post(() => AppendOutput(data)),
                            _runCts.Token);
                    }
                }, _runCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendOutputError(ex); }
            finally
            {
                _runningHost = null;
                await _runCts.CancelAsync();
                model.EnableRunButton    = true;
                model.EnableCancelButton = false;
                model.IsDebugging        = false;
                model.IsPaused           = false;
                if (debug) model.DebugInfo = "Debug session ended.";
            }
        }
        catch (Exception e)
        {
            AppendOutputError(e);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        // If paused, resume first so the background task isn't deadlocked.
        _currentDebugContext?.Resume(DebugAction.Continue);
        _currentDebugContext = null;
        _runCts?.Cancel();
    }

    // ── Breakpoint hit ────────────────────────────────────────────────────────

    private void OnBreakpointHit(IBreakpointHitContext context)
    {
        _currentDebugContext = context;
        GetViewModel().IsPaused = true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"⏸ Paused at line {context.Breakpoint.Line}  (thread {context.ThreadId})");

        if (context.Threads.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Threads:");
            foreach (var t in context.Threads)
                sb.AppendLine($"  [{t.Id}] {t.Name}");
        }

        if (context.LocalVariables.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Locals:");
            foreach (var v in context.LocalVariables)
                sb.AppendLine($"  {v.TypeName} {v.Name} = {v.Value}");
        }

        if (context.CallStack.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Call stack:");
            foreach (var frame in context.CallStack)
                sb.AppendLine($"  {frame.MethodName}  ({Path.GetFileName(frame.FileName)}:{frame.Line})");
        }

        GetViewModel().DebugInfo = sb.ToString();
    }

    // ── Debug control buttons ────────────────────────────────────────────────

    private void DbgContinue_Click(object? sender, RoutedEventArgs e)  => ResumeDebugger(DebugAction.Continue);
    private void DbgStepOver_Click(object? sender, RoutedEventArgs e)  => ResumeDebugger(DebugAction.StepOver);
    private void DbgStepIn_Click(object? sender, RoutedEventArgs e)    => ResumeDebugger(DebugAction.StepIn);
    private void DbgStepOut_Click(object? sender, RoutedEventArgs e)   => ResumeDebugger(DebugAction.StepOut);

    private void ResumeDebugger(DebugAction action)
    {
        var model = GetViewModel();
        model.IsPaused  = false;
        model.DebugInfo = $"Running… (last action: {action})";

        var ctx = _currentDebugContext;
        _currentDebugContext = null;
        ctx?.Resume(action);
    }

    private async void DbgPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_runningHost is not null)
            await _runningHost.PauseAsync();
    }

    // ── Misc UI ───────────────────────────────────────────────────────────────

    private void AddControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textEditor is null) return;
        _textEditor.InlineControls.Controls.Add(
            new KeyValuePair<int, Control>(_textEditor.CaretOffset,
                new Button { Content = "Click me", Cursor = Cursor.Default }));
        _textEditor.TextArea.TextView.Redraw();
    }

    private void ClearControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_textEditor is null) return;
        _textEditor.InlineControls.Controls.Clear();
        _textEditor.TextArea.TextView.Redraw();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
        => _serviceProvider.GetRequiredService<PreferencesWindow>().ShowDialog(this);

    public void Dispose()
    {
        _themeService.ThemeChange -= OnThemeChanged;
        _runCts?.Cancel();
        _runCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetTab(int index)
    {
        if (_outputTabs is not null)
            _outputTabs.SelectedIndex = index;
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private void AppendOutput(string data)
    {
        _htmlPanel?.Text += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
    }

    private void AppendOutputError(Exception ex)
    {
        var message = HttpUtility.HtmlEncode(ex.Message);
        var html    = $"\n<div style=\"color:red\">{message}</div>";

        if (ex.StackTrace is { } st)
        {
            var trace = HttpUtility.HtmlEncode(st);
            html += $"\n<pre style=\"color:#c04040;font-size:0.85em\">{trace}</pre>";
        }

        _htmlPanel?.Text += html;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string EnsureWorkDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DotPad");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private CSharpLanguageHostOptions BuildRunOptions(MainWindowViewModel model, string workDir) => new()
    {
        Platform         = model.SelectedDotnetPlatform ?? model.DotnetPlatforms.First(),
        ExecutionOptions = new ExecutionOptions
        {
            WorkingDirectory = workDir,
            Runtime          = new RuntimeOptions { RuntimePath = _optionsService.DotnetPath },
            Debugger         = new DebuggerOptions { DebuggerPath = _optionsService.DebuggerPath },
        },
        OutputDirectory = Path.Combine(workDir, Path.GetRandomFileName()),
        RuntimeLibPath  = new CSharpReferenceService().GetExtensionReference(),
    };

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException(
                $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");

    private sealed class ScriptBreakpoint(int line) : DotPad.Contracts.IBreakpoint
    {
        public string FileName { get; } = string.Empty;
        public int Line { get; set; } = line;
    }
}
