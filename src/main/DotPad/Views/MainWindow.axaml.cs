using System.Diagnostics;
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
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
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
    private IIntelliSenseService? _intelliSenseService;

    private CodeTextEditor? _textEditor;
    private HtmlPanel? _htmlPanel;
    private TextBlock? _statusText;
    private TextBlock? _elapsedText;
    private TextBlock? _executionStatusText;
    private TabControl? _outputTabs;
    private CancellationTokenSource? _runCts;
    private readonly Stopwatch _runStopwatch = new();
    private bool _hadRunError;
    private string _outputCss     = "";
    private string _outputContent = "";

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
        _outputCss     = HtmlThemeStylesheet.Generate(_themeService.CurrentTheme.WindowTheme);
        UpdateHtmlOutput();
    }

    private void ApplyHtmlTheme(Theme theme)
    {
        _outputCss = HtmlThemeStylesheet.Generate(theme.WindowTheme);
        UpdateHtmlOutput();
    }

    private void UpdateHtmlOutput()
    {
        if (_htmlPanel is null) 
        {
            return;
        }

        var htmlContent = $@"
            <html>
            <head>
                <style>{_outputCss}</style>
            </head>
            <body>
                {_outputContent}
            </body>
            </html>";
        _htmlPanel.Text = htmlContent;
    }

    private void InitializeCodeTextEditor()
    {
        _textEditor          = this.FindControl<CodeTextEditor>("Editor");
        _statusText          = this.Find<TextBlock>("StatusText");
        _elapsedText         = this.Find<TextBlock>("ElapsedText");
        _executionStatusText = this.Find<TextBlock>("ExecutionStatusText");
        _outputTabs          = this.FindControl<TabControl>("OutputTabs");
        if (_textEditor is null) return;

        _textEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        _textEditor.BreakpointAdded   += (_, e) => Console.WriteLine($"BP added at line {e.Line}");
        _textEditor.BreakpointRemoved += (_, e) => Console.WriteLine($"BP removed at line {e.Line}");

        _intelliSenseService = new CSharpIntelliSenseService(
            new CSharpReferenceService().GetDefault());

        _textEditor.CompletionTriggers.Add('.');
        _textEditor.OverloadTriggers.Add('(');
        _textEditor.AsyncCompletionProvider = OnCompletionRequestedAsync;
        _textEditor.AsyncOverloadProvider   = OnOverloadRequestedAsync;

        _textEditor.TextArea.IndentationStrategy =
            new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(_textEditor.Options);

        var scopeName = _textEditor.SetLanguage(_themeService.CurrentTheme.CodeEditorThemeName, ".cs");
        _textEditor.Document = new TextDocument(ResourceLoader.LoadSampleFile(scopeName));
    }

    private void OnThemeChanged(Theme theme)
    {
        _textEditor?.SetTheme(theme.CodeEditorThemeName);
        ApplyHtmlTheme(theme);
    }

    private async Task OnCompletionRequestedAsync(CompletionRequestedEventArgs e)
    {
        if (_intelliSenseService is null || _textEditor is null) return;
        var trigger = CompletionTrigger.Insertion(e.Trigger);
        var list = await _intelliSenseService.GetCompletionsAsync(_textEditor.Text, e.CaretOffset, trigger);
        if (list is null) return;
        foreach (var item in list.Items)
            e.Items.Add(new EditorCompletionData(item));
    }

    private async Task OnOverloadRequestedAsync(OverloadRequestedEventArgs e)
    {
        if (_intelliSenseService is null || _textEditor is null) return;
        var result = await _intelliSenseService.GetSignatureHelpAsync(_textEditor.Text, e.CaretOffset, e.Trigger);
        if (result is null) return;
        foreach (var item in result.Items)
            e.Overloads.Add((item.DisplaySignature, item.Documentation));
    }

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
            model.IsRunningProgress  = true;
            _outputContent = "";
            _hadRunError   = false;
            UpdateHtmlOutput();
            if (_elapsedText is not null) _elapsedText.Text = "";
            SetExecutionStatus("Process", "#4FC1FF");
            _runStopwatch.Restart();

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

            var cancelled = false;
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
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { AppendOutputError(ex); }
            finally
            {
                _runningHost = null;
                await _runCts.CancelAsync();
                model.EnableRunButton    = true;
                model.EnableCancelButton = false;
                model.IsDebugging        = false;
                model.IsPaused           = false;
                model.IsRunningProgress  = false;
                if (debug) model.DebugInfo = "Debug session ended.";
                _runStopwatch.Stop();
                if (_elapsedText is not null)
                    _elapsedText.Text = FormatElapsed(_runStopwatch.Elapsed);
                if (cancelled)
                    SetExecutionStatus("Cancelled", "#9E9E9E");
                else if (!_hadRunError)
                    SetExecutionStatus("Success", "#4EC94E");
                // Error status already set inside AppendOutputError
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

        var vm = GetViewModel();
        vm.DebugInfo          = sb.ToString();
        vm.IsRunningProgress  = false;
        SetExecutionStatus("Paused", "#FFA500");
    }

    // ── Debug control buttons ────────────────────────────────────────────────

    private void DbgContinue_Click(object? sender, RoutedEventArgs e)  => ResumeDebugger(DebugAction.Continue);
    private void DbgStepOver_Click(object? sender, RoutedEventArgs e)  => ResumeDebugger(DebugAction.StepOver);
    private void DbgStepIn_Click(object? sender, RoutedEventArgs e)    => ResumeDebugger(DebugAction.StepIn);
    private void DbgStepOut_Click(object? sender, RoutedEventArgs e)   => ResumeDebugger(DebugAction.StepOut);

    private void ResumeDebugger(DebugAction action)
    {
        var model = GetViewModel();
        model.IsPaused           = false;
        model.IsRunningProgress  = true;
        model.DebugInfo          = $"Running… (last action: {action})";
        SetExecutionStatus("Process", "#4FC1FF");

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
        _intelliSenseService?.Dispose();
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
        _outputContent += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
        UpdateHtmlOutput();
    }

    private void AppendOutputError(Exception ex)
    {
        _hadRunError = true;
        SetExecutionStatus("Error", "#F14C4C");
        var message = HttpUtility.HtmlEncode(ex.Message);
        _outputContent += $"\n<div style=\"color:#f14c4c\">{message}</div>";

        if (ex.StackTrace is { } st)
        {
            var trace = HttpUtility.HtmlEncode(st);
            _outputContent += $"\n<pre style=\"color:#cd9591;font-size:0.85em\">{trace}</pre>";
        }

        UpdateHtmlOutput();
    }

    private void SetExecutionStatus(string text, string hex)
    {
        if (_executionStatusText is null) return;
        _executionStatusText.Text       = text;
        _executionStatusText.Foreground = string.IsNullOrEmpty(hex)
            ? Brushes.Transparent
            : new SolidColorBrush(Color.Parse(hex));
    }

    private static string FormatElapsed(TimeSpan t) => t switch
    {
        { TotalSeconds: < 1  } => $"{t.TotalMilliseconds:F0} ms",
        { TotalMinutes: < 1  } => $"{t.TotalSeconds:F2} s",
        _                      => $"{(int)t.TotalMinutes}m {t.Seconds}s",
    };

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
