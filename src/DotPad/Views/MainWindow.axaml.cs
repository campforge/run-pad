using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.HtmlRenderer;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Resources;
using DotPad.ViewModels;
using static AvaloniaEdit.TextMate.TextMate;

namespace DotPad.Views;

using Pair = KeyValuePair<int, Control>;

public partial class MainWindow : Window, IDisposable
{
    private readonly CodeTextEditor _textEditor;
    private readonly Installation _textMateInstallation;
    private CompletionWindow _completionWindow;
    private OverloadInsightWindow _insightWindow;
    private readonly TextBlock _statusTextBlock;
    private readonly ElementGenerator _generator = new();
    private readonly RegistryOptions _registryOptions;
    private int _currentTheme = (int)ThemeName.DarkPlus;

    private CancellationTokenSource? _runTaskCancellationTokenSource;

    public MainWindow()
    {
            InitializeComponent();

            // htmlPanel
            HtmlPanel = this.FindControl<HtmlPanel>("HtmlPanel");
            if (HtmlPanel != null)
            {
                HtmlPanel.Container.AddFontFamily(new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));
                HtmlPanel.Text = $"\n<div>Init</div>";;
                //_htmlPanel.Background = Brush.Parse("#000");
            }
            // textEditor
            _textEditor = this.FindControl<CodeTextEditor>("Editor");
            if (_textEditor != null)
            {
                _textEditor.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
                _textEditor.Background = Brushes.Transparent;
                _textEditor.ShowLineNumbers = true;
                _textEditor.ShowBreakpoints = true;

                _textEditor.TextArea.Background = this.Background;
                _textEditor.TextArea.TextEntered += textEditor_TextArea_TextEntered;
                _textEditor.TextArea.TextEntering += textEditor_TextArea_TextEntering;
                _textEditor.Options.AllowToggleOverstrikeMode = true;
                _textEditor.Options.ShowBoxForControlCharacters = true;
                _textEditor.Options.ColumnRulerPositions = new List<int>() { 80, 100 };
                //_textEditor.TextArea.IndentationStrategy = new Indentation.CSharp.CSharpIndentationStrategy(_textEditor.Options);
                _textEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
                _textEditor.TextArea.RightClickMovesCaret = true;


                _textEditor.TextArea.TextView.ElementGenerators.Add(_generator);

                _registryOptions = new RegistryOptions(
                    (ThemeName)_currentTheme);

                _textMateInstallation = _textEditor.InstallTextMate(_registryOptions);
                var csharpLanguage = _registryOptions.GetLanguageByExtension(".cs");

                string scopeName = _registryOptions.GetScopeByLanguageId(csharpLanguage.Id);

                _textEditor.Document = new TextDocument(
                    "// DotPad.Avalonia.AvaloniaEdit supports displaying control chars: \a or \b or \v" + Environment.NewLine +
                    ResourceLoader.LoadSampleFile(scopeName));
                _textMateInstallation.SetGrammar(_registryOptions.GetScopeByLanguageId(csharpLanguage.Id));
                _textEditor.TextArea.TextView.LineTransformers.Add(new UnderlineAndStrikeThroughTransformer());
                _textEditor.TextArea.IndentationStrategy = new AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy(_textEditor.Options);

                _statusTextBlock = this.Find<TextBlock>("StatusText");

                this.AddHandler(PointerWheelChangedEvent, (o, i) =>
                {
                    if (i.KeyModifiers != KeyModifiers.Control) return;
                    if (i.Delta.Y > 0) _textEditor.FontSize++;
                    else _textEditor.FontSize = _textEditor.FontSize > 1 ? _textEditor.FontSize - 1 : 1;
                }, RoutingStrategies.Bubble, true);
            }
        }

    private void Caret_PositionChanged(object sender, EventArgs e)
    {
            _statusTextBlock.Text = string.Format("Line {0} Column {1}",
                _textEditor.TextArea.Caret.Line,
                _textEditor.TextArea.Caret.Column);
        }

    protected override void OnClosed(EventArgs e)
    {
            base.OnClosed(e);

            _textMateInstallation.Dispose();
        }

    private void ChangeThemeButton_Click(object sender, RoutedEventArgs e)
    {
            _currentTheme = (_currentTheme + 1) % Enum.GetNames(typeof(ThemeName)).Length;

            _textMateInstallation.SetTheme(_registryOptions.LoadTheme((ThemeName)_currentTheme));
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
        Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(600, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
            }
            finally
            {
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
        if(_runTaskCancellationTokenSource == null || _runTaskCancellationTokenSource.IsCancellationRequested)
            return;
    
        _runTaskCancellationTokenSource?.Cancel();
        return;
        // ;
        // HtmlPanel.Text = string.Empty;
        // var root = Path.Combine(Path.GetTempPath(), "DotPad");
        // if (!Directory.Exists(root))
        //     Directory.CreateDirectory(root);
        // var rs = new CSharpReferenceService();
        // var sdk = model?.DotnetPlatforms.First(i => i.Description == model.SelectedDotnetPlatform);
        // var refs = rs.GetDefault().ToList();
        // var runtimeLib = rs.GetExtensionReference();
        // refs.Add(runtimeLib);
        //
        // var cs = new CSharpExecutionHostService(root,
        //     runtimeLib,
        //     sdk);
        //
        //
        // var ct = new CancellationToken();
        //
        // HtmlPanel.Text += $"\n<div>Execute</div>";
        // var script = _textEditor.Text;
        //
        // cs.DataReceived += (sender, args) =>
        // {
        //     Dispatcher.UIThread.Post(() =>
        //     {
        //         HtmlPanel.Text += $"\n<div>{HttpUtility.HtmlEncode(args.Data)}</div>";
        //     });
        // };
        //
        // Task.Run(async () =>
        // {
        //
        //     Dispatcher.UIThread.Post(() => { model!.EnableRunButton = false; });
        //     try
        //     {
        //         await cs.Execute(script, refs.ToArray(), ct);
        //     }
        //     catch (Exception exception)
        //     {
        //         await Dispatcher.UIThread.InvokeAsync(() =>
        //         {
        //             HtmlPanel.Text += $"\n<div>{exception.Message}</div>";
        //         });
        //     }
        //     finally
        //     {
        //         Dispatcher.UIThread.Post(() => { model!.EnableRunButton = true; });
        //     }
        // }, ct);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AddControlButton_Click(object sender, RoutedEventArgs e)
    {
            _generator.controls.Add(new Pair(_textEditor.CaretOffset, new Button() { Content = "Click me", Cursor = Cursor.Default }));
            _textEditor.TextArea.TextView.Redraw();
        }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
            var ownerWindow = this;
            var window = new PreferencesWindow();
            window.DataContext = new PreferencesWindowViewModel();
            window.ShowDialog(ownerWindow);
        }


    private void ClearControlButton_Click(object sender, RoutedEventArgs e)
    {
            //TODO: delete elements using back key
            _generator.controls.Clear();
            _textEditor.TextArea.TextView.Redraw();
        }

    private void textEditor_TextArea_TextEntering(object sender, TextInputEventArgs e)
    {
            if (e.Text.Length > 0 && _completionWindow != null)
            {
                if (!char.IsLetterOrDigit(e.Text[0]))
                {
                    // Whenever a non-letter is typed while the completion window is open,
                    // insert the currently selected element.
                    _completionWindow.CompletionList.RequestInsertion(e);
                }
            }

            _insightWindow?.Hide();

            // Do not set e.Handled=true.
            // We still want to insert the character that was typed.
        }



    private void textEditor_TextArea_TextEntered(object sender, TextInputEventArgs e)
    {
            if (e.Text == ".")
            {

                _completionWindow = new CompletionWindow(_textEditor.TextArea);
                _completionWindow.Closed += (o, args) => _completionWindow = null;

                var data = _completionWindow.CompletionList.CompletionData;
                data.Add(new MyCompletionData("Item1"));
                data.Add(new MyCompletionData("Item2"));


                _completionWindow.Show();
            }
            else if (e.Text == "(")
            {
                _insightWindow = new OverloadInsightWindow(_textEditor.TextArea);
                _insightWindow.Closed += (o, args) => _insightWindow = null;

                _insightWindow.Provider = new MyOverloadProvider(new[]
                {
                    ("Method1(int, string)", "Method1 description"),
                    ("Method2(int)", "Method2 description"),
                    ("Method3(string)", "Method3 description"),
                });

                _insightWindow.Show();
            }
        }

    class UnderlineAndStrikeThroughTransformer : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
                if (line.LineNumber == 2)
                {
                    string lineText = this.CurrentContext.Document.GetText(line);

                    int indexOfUnderline = lineText.IndexOf("underline");
                    int indexOfStrikeThrough = lineText.IndexOf("strikethrough");

                    if (indexOfUnderline != -1)
                    {
                        ChangeLinePart(
                            line.Offset + indexOfUnderline,
                            line.Offset + indexOfUnderline + "underline".Length,
                            visualLine =>
                            {
                                if (visualLine.TextRunProperties.TextDecorations != null)
                                {
                                    var textDecorations = new TextDecorationCollection(visualLine.TextRunProperties.TextDecorations) { TextDecorations.Underline[0] };

                                    visualLine.TextRunProperties.SetTextDecorations(textDecorations);
                                }
                                else
                                {
                                    visualLine.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                                }
                            }
                        );
                    }

                    if (indexOfStrikeThrough != -1)
                    {
                        ChangeLinePart(
                            line.Offset + indexOfStrikeThrough,
                            line.Offset + indexOfStrikeThrough + "strikethrough".Length,
                            visualLine =>
                            {
                                if (visualLine.TextRunProperties.TextDecorations != null)
                                {
                                    var textDecorations = new TextDecorationCollection(visualLine.TextRunProperties.TextDecorations) { TextDecorations.Strikethrough[0] };

                                    visualLine.TextRunProperties.SetTextDecorations(textDecorations);
                                }
                                else
                                {
                                    visualLine.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                                }
                            }
                        );
                    }
                }
            }
    }

    private class MyOverloadProvider : IOverloadProvider
    {
        private readonly IList<(string header, string content)> _items;
        private int _selectedIndex;

        public MyOverloadProvider(IList<(string header, string content)> items)
        {
                _items = items;
                SelectedIndex = 0;
            }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                    _selectedIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CurrentHeader));
                    OnPropertyChanged(nameof(CurrentContent));
                }
        }

        public int Count => _items.Count;
        public string CurrentIndexText => $"{SelectedIndex + 1} of {Count}";
        public object CurrentHeader => _items[SelectedIndex].header;
        public object CurrentContent => _items[SelectedIndex].content;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
    }

    public class MyCompletionData : ICompletionData
    {
        public MyCompletionData(string text)
        {
                Text = text;
            }

        public IImage Image => null;

        public string Text { get; }

        // Use this property if you want to show a fancy UIElement in the list.
        public object Content => Text;

        public object Description => "Description for " + Text;

        public double Priority { get; } = 0;

        public void Complete(TextArea textArea, ISegment completionSegment,
            EventArgs insertionRequestEventArgs)
        {
                textArea.Document.Replace(completionSegment, Text);
            }
    }

    class ElementGenerator : VisualLineElementGenerator, IComparer<Pair>
    {
        public readonly List<Pair> controls = new List<Pair>();

        /// <summary>
        /// Gets the first interested offset using binary search
        /// </summary>
        /// <returns>The first interested offset.</returns>
        /// <param name="startOffset">Start offset.</param>
        public override int GetFirstInterestedOffset(int startOffset)
        {
                int pos = controls.BinarySearch(new Pair(startOffset, null), this);
                if (pos < 0)
                    pos = ~pos;
                if (pos < controls.Count)
                    return controls[pos].Key;
                else
                    return -1;
            }

        public override VisualLineElement ConstructElement(int offset)
        {
                int pos = controls.BinarySearch(new Pair(offset, null), this);
                if (pos >= 0)
                    return new InlineObjectElement(0, controls[pos].Value);
                else
                    return null;
            }

        int IComparer<Pair>.Compare(Pair x, Pair y)
        {
                return x.Key.CompareTo(y.Key);
            }
    }

    public void Dispose()
    {
        _runTaskCancellationTokenSource?.Cancel();
    }
}