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
    private CancellationTokenSource? _runCts;

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
        _textEditor  = this.FindControl<CodeTextEditor>("Editor");
        _statusText  = this.Find<TextBlock>("StatusText");
        if (_textEditor is null) return;

        _textEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
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

    private async void RunButton_Click(object sender, RoutedEventArgs eventsArgs)
    {
        try
        {
            if (_textEditor is null) return;
            if (_runCts is { IsCancellationRequested: false }) return;

            var model = GetViewModel();
            model.EnableRunButton    = false;
            model.EnableCancelButton = true;

            _runCts = new CancellationTokenSource();
            _htmlPanel?.Text = string.Empty;

            var workDir = EnsureWorkDirectory();
            var options = BuildRunOptions(model, workDir);
            var script  = _textEditor.Text;

            try
            {
                await Task.Run(async () =>
                {
                    await using var host = _registry.Create("csharp", options);
                    await host.RunAsync(script, (data, _) =>
                            Dispatcher.UIThread.Post(() => AppendOutput(data)),
                        _runCts.Token);
                }, _runCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppendOutput(ex.Message);
            }
            finally
            {
                await _runCts.CancelAsync();
                model.EnableRunButton    = true;
                model.EnableCancelButton = false;
            }
        }
        catch (Exception e)
        {
            AppendOutput(e.Message);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => _runCts?.Cancel();

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

    private void AppendOutput(string data)
    {
        _htmlPanel?.Text += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
    }

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
        },
        OutputDirectory = Path.Combine(workDir, Path.GetRandomFileName()),
        RuntimeLibPath  = new CSharpReferenceService().GetExtensionReference(),
    };

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException(
                $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");
}
