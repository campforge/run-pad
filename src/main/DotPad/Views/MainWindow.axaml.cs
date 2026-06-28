using System.Web;
using Avalonia.Controls;
using Avalonia.HtmlRenderer;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using Microsoft.Extensions.DependencyInjection;
using DotPad.Languages.CSharp;
using DotPad.Models;
using DotPad.Resources;
using DotPad.Services;
using DotPad.ViewModels;

namespace DotPad.Views;

public partial class MainWindow : Window, IDisposable
{
    private CodeTextEditor? _textEditor;
    private TextBlock? _statusTextBlock;
    private readonly IThemeService _themeService;
    private CancellationTokenSource? _runTaskCancellationTokenSource;
    private readonly ILanguageHostRegistry _registry;
    private readonly IOptionsService _optionsService;
    private readonly IServiceProvider _serviceProvider;

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

        _themeService.ThemeChange += ThemeServiceOnThemeChange;

        InitializeComponent();
        InitializeHtmlPanel();
        InitializeCodeTextEditor();
    }

    private void InitializeHtmlPanel()
    {
        HtmlPanel = this.FindControl<HtmlPanel>("HtmlPanel");
        if (HtmlPanel is null) return;

        HtmlPanel.Container.AddFontFamily(new FontFamily(
            new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));
        HtmlPanel.Text = $"\n<div>Init</div>";
    }

    private void InitializeCodeTextEditor()
    {
        _textEditor = this.FindControl<CodeTextEditor>("Editor");
        if (_textEditor is null) return;

        _textEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
        _textEditor.TextArea.IndentationStrategy =
            new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(_textEditor.Options);

        var scopeName = _textEditor.SetLanguage(_themeService.CurrentTheme.CodeEditorThemeName, ".cs");
        _textEditor.Document = new TextDocument(ResourceLoader.LoadSampleFile(scopeName));

        _statusTextBlock = this.Find<TextBlock>("StatusText");
    }

    private void ThemeServiceOnThemeChange(Theme theme)
    {
        _textEditor?.SetTheme(theme.CodeEditorThemeName);
    }

    private void Caret_PositionChanged(object? sender, EventArgs e)
    {
        if (_textEditor != null && _statusTextBlock != null)
            _statusTextBlock.Text = string.Format("Line {0} Column {1}",
                _textEditor.TextArea.Caret.Line,
                _textEditor.TextArea.Caret.Column);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    }

    private MainWindowViewModel GetDataContext()
    {
        var model = DataContext as MainWindowViewModel;
        if (model == null)
        {
            throw new NullReferenceException($"{DataContext} is null");
        }

        return model;
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runTaskCancellationTokenSource is { IsCancellationRequested: false })
            return;

        var model = GetDataContext();
        model.EnableRunButton = false;
        model.EnableCancelButton = true;
        _runTaskCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _runTaskCancellationTokenSource.Token;

        HtmlPanel.Text = string.Empty;
        var root = Path.Combine(Path.GetTempPath(), "DotPad");
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);

        var sdk = model.SelectedDotnetPlatform ?? model.DotnetPlatforms.First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();

        var options = new CSharpLanguageHostOptions
        {
            Platform         = sdk,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = root,
                Runtime          = new RuntimeOptions { RuntimePath = _optionsService.DotnetPath },
            },
            OutputDirectory = Path.Combine(root, Path.GetRandomFileName()),
            RuntimeLibPath  = runtimeLib,
        };

        HtmlPanel.Text += $"\n<div>Execute</div>";
        var script = _textEditor!.Text;

        Task.Run(async () =>
        {
            try
            {
                await using var host = _registry.Create("csharp", options);
                await host.RunAsync(script, (data, kind) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        HtmlPanel.Text += $"\n<div>{HttpUtility.HtmlEncode(data)}</div>";
                    });
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    HtmlPanel.Text += $"\n<div>{exception.Message}</div>";
                });
            }
            finally
            {
                _runTaskCancellationTokenSource.Cancel();
                Dispatcher.UIThread.Post(() =>
                {
                    model.EnableRunButton = true;
                    model.EnableCancelButton = false;
                });
            }

        }, cancellationToken);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runTaskCancellationTokenSource == null || _runTaskCancellationTokenSource.IsCancellationRequested)
            return;

        _runTaskCancellationTokenSource?.Cancel();
    }

    private void AddControlButton_Click(object sender, RoutedEventArgs e)
    {
        _textEditor!.InlineControls.Controls.Add(new KeyValuePair<int, Control>(_textEditor!.CaretOffset, new Button() { Content = "Click me", Cursor = Cursor.Default }));
        _textEditor!.TextArea.TextView.Redraw();
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        _serviceProvider.GetRequiredService<PreferencesWindow>().ShowDialog(this);
    }

    private void ClearControlButton_Click(object sender, RoutedEventArgs e)
    {
        _textEditor!.InlineControls.Controls.Clear();
        _textEditor!.TextArea.TextView.Redraw();
    }

    public void Dispose()
    {
        _runTaskCancellationTokenSource?.Cancel();
    }
}
