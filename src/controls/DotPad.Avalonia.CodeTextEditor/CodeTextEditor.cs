using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using AvaloniaEdit.Utils;
using TextMateSharp.Grammars;
using static AvaloniaEdit.TextMate.TextMate;

namespace DotPad.Avalonia.CodeTextEditor;

public class CodeTextEditor : TextEditor
{
    private BreakPointMargin _breakpointMargin;
    private CodeTextEditorOptions _editorOptions = new();
    private RegistryOptions? _registryOptions;
    private Installation? _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;

    public ElementGenerator InlineControls { get; } = new();

    public CodeTextEditorOptions EditorOptions
    {
        get => _editorOptions;
        set { _editorOptions = value; ApplyOptions(); }
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public CodeTextEditor()
    {
        ShowBreakpointsProperty.Changed.Subscribe(OnShowBreakpointsChanged);
        _breakpointMargin = new BreakPointMargin(this, new BreakpointStore());

        BindEditorCommands();
        InitContextMenu();
        Initialize();
    }

    private void BindEditorCommands()
    {
        var commandBindings = TextArea.CommandBindings;
        var deleteLineCommand = commandBindings.OfType<RoutedCommandBinding>()
            .FirstOrDefault(x => x.Command == AvaloniaEditCommands.DeleteLine);

        if (deleteLineCommand != null)
            commandBindings.Remove(deleteLineCommand);
    }

    private void InitContextMenu()
    {
        ContextMenu = new ContextMenu
        {
            ItemsSource = new List<MenuItem>
            {
                new() { Header = "Copy",  InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) },
                new() { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) },
                new() { Header = "Cut",   InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) }
            }
        };
    }

    private void Initialize()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        ShowLineNumbers = true;

        Options.AllowToggleOverstrikeMode = true;
        Options.ShowBoxForControlCharacters = true;
        TextArea.RightClickMovesCaret = true;

        ApplyOptions();

        TextArea.TextView.LineTransformers.Add(new UnderlineAndStrikeThroughTransformer());
        TextArea.TextView.ElementGenerators.Add(InlineControls);

        TextArea.TextEntered  += TextArea_TextEntered;
        TextArea.TextEntering += TextArea_TextEntering;

        AddHandler(PointerWheelChangedEvent, OnFontSizeScroll, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void ApplyOptions()
    {
        ShowBreakpoints = _editorOptions.ShowBreakpoints;
        Options.ColumnRulerPositions = _editorOptions.ColumnRulerPositions;
    }

    private void OnFontSizeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Delta.Y > 0) FontSize++;
        else FontSize = FontSize > 1 ? FontSize - 1 : 1;
    }

    public static readonly StyledProperty<bool> ShowBreakpointsProperty =
        AvaloniaProperty.Register<TextEditor, bool>("ShowBreakpoints");

    public bool ShowBreakpoints
    {
        get => GetValue(ShowBreakpointsProperty);
        set => SetValue(ShowBreakpointsProperty, value);
    }

    private void OnShowBreakpointsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var editor = e.Sender as CodeTextEditor;
        if (editor == null) return;

        var leftMargins = editor.TextArea.LeftMargins;

        if (e.NewValue != null && (bool)e.NewValue)
            leftMargins.Insert(1, _breakpointMargin);
        else
            leftMargins.Remove(_breakpointMargin);
    }

    public string SetLanguage(ThemeName theme, string languageExtension)
    {
        _registryOptions = new RegistryOptions(theme);
        _textMateInstallation?.Dispose();
        _textMateInstallation = this.InstallTextMate(_registryOptions);

        var language = _registryOptions.GetLanguageByExtension(languageExtension);
        var scopeName = _registryOptions.GetScopeByLanguageId(language.Id);
        _textMateInstallation.SetGrammar(scopeName);
        return scopeName;
    }

    public void SetTheme(ThemeName theme)
    {
        if (_textMateInstallation is null || _registryOptions is null) return;
        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(theme));
    }

    public int GetOffsetFromPoint(Point point)
    {
        var position = GetPositionFromPoint(point);
        return position != null ? Document.GetOffset(position.Value.Location) : -1;
    }

    private void TextArea_TextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } text && _completionWindow != null)
        {
            if (!char.IsLetterOrDigit(text[0]))
                _completionWindow.CompletionList.RequestInsertion(e);
        }

        _insightWindow?.Hide();
    }

    private void TextArea_TextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
        {
            _completionWindow = new CompletionWindow(TextArea);
            _completionWindow.Closed += (_, _) => _completionWindow = null;

            var data = _completionWindow.CompletionList.CompletionData;
            data.Add(new CompletionData("Item1"));
            data.Add(new CompletionData("Item2"));

            _completionWindow.Show();
        }
        else if (e.Text == "(")
        {
            _insightWindow = new OverloadInsightWindow(TextArea);
            _insightWindow.Closed += (_, _) => _insightWindow = null;

            _insightWindow.Provider = new OverloadProvider(new[]
            {
                ("Method1(int, string)", "Method1 description"),
                ("Method2(int)",         "Method2 description"),
                ("Method3(string)",      "Method3 description"),
            });

            _insightWindow.Show();
        }
    }
}

public sealed class UnderlineAndStrikeThroughTransformer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber != 2) return;

        var text = CurrentContext.Document.GetText(line);
        Highlight(line, text, "underline",     TextDecorations.Underline);
        Highlight(line, text, "strikethrough", TextDecorations.Strikethrough);
    }

    private void Highlight(DocumentLine line, string text, string word, TextDecorationCollection decoration)
    {
        int index = text.IndexOf(word, StringComparison.Ordinal);
        if (index == -1) return;

        ChangeLinePart(line.Offset + index, line.Offset + index + word.Length, vl =>
        {
            var existing = vl.TextRunProperties.TextDecorations;
            vl.TextRunProperties.SetTextDecorations(
                existing != null
                    ? new TextDecorationCollection(existing) { decoration[0] }
                    : decoration);
        });
    }
}

public sealed class ElementGenerator : VisualLineElementGenerator, IComparer<KeyValuePair<int, Control>>
{
    public readonly List<KeyValuePair<int, Control>> Controls = new();

    public override int GetFirstInterestedOffset(int startOffset)
    {
        int pos = Controls.BinarySearch(new KeyValuePair<int, Control>(startOffset, null!), this);
        if (pos < 0) pos = ~pos;
        return pos < Controls.Count ? Controls[pos].Key : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        int pos = Controls.BinarySearch(new KeyValuePair<int, Control>(offset, null!), this);
        return pos >= 0 ? new InlineObjectElement(0, Controls[pos].Value) : null;
    }

    int IComparer<KeyValuePair<int, Control>>.Compare(KeyValuePair<int, Control> x, KeyValuePair<int, Control> y)
        => x.Key.CompareTo(y.Key);
}

public sealed class CompletionData : ICompletionData
{
    public CompletionData(string text) => Text = text;

    public IImage? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description => "Description for " + Text;
    public double Priority { get; } = 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}

public sealed class OverloadProvider : IOverloadProvider
{
    private readonly IList<(string header, string content)> _items;
    private int _selectedIndex;

    public OverloadProvider(IList<(string header, string content)> items)
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BreakPointMargin : AbstractMargin, IDisposable
{
    private IBreakpointStore _manager;
    private CodeTextEditor _editor;

    private int _previewLine;
    private bool _previewPointVisible;

    private Pen LinePen;

    static BreakPointMargin()
    {
        FocusableProperty.OverrideDefaultValue(typeof(BreakPointMargin), false);
    }

    public BreakPointMargin(CodeTextEditor editor, IBreakpointStore manager)
    {
        _manager = manager;
        _editor = editor;
        Cursor = new Cursor(StandardCursorType.Hand);
        LinePen = new Pen(Brushes.DarkRed, 0.5);
        Focusable = false;
    }

    private static Point CalculatePosition(VisualLine visualLine, Size size, double verticalOffset)
    {
        var lineVisualYPosition = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.LineTop);
        var x = (size.Width / 4) - 1;
        var y = lineVisualYPosition + (size.Width / 4) - 3.5 - verticalOffset;
        return new Point(x, y);
    }

    private void CreatePoint(DrawingContext context, VisualLine line, double height, IBrush brush)
    {
        var size = Bounds.Size;
        var point = CalculatePosition(line, size, TextView.VerticalOffset);
        context.FillRectangle(brush,
            new Rect(point.X, point.Y, size.Width / 1.5, height / 1.5), (float)height);
    }

    public override void Render(DrawingContext context)
    {
        if (!TextView.VisualLinesValid)
            return;

        context.FillRectangle(_editor?.Background ?? Brushes.Honeydew, Bounds);
        context.DrawLine(LinePen, Bounds.TopRight, Bounds.BottomRight);
        if (TextView.VisualLines.Count <= 0)
            return;

        var firstLine = TextView.VisualLines.FirstOrDefault();
        var height = firstLine.Height;

        foreach (var breakPoint in _manager?.OfType<IBreakpoint>())
        {
            var visualLine = TextView.VisualLines
                .FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == breakPoint.Line);
            if (visualLine != null)
                CreatePoint(context, visualLine, height, Brush.Parse("#FF3737"));
        }

        if (!_previewPointVisible && _previewLine >= 0)
            return;
        {
            var visualLine = TextView.VisualLines
                .FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == _previewLine);
            if (visualLine != null)
                CreatePoint(context, visualLine, height, Brush.Parse("#E67466"));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (UpdateLines(e))
            return;
        InvalidateVisual();
    }

    private bool UpdateLines(PointerEventArgs e)
    {
        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));

        if (offset == -1)
            return true;

        var lineNumber = TextView.Document.GetLineByOffset(offset).LineNumber;

        if (_previewPointVisible && lineNumber == _previewLine)
            return true;

        if (_manager.Any(i => i.Line == lineNumber))
            _previewPointVisible = false;
        else
        {
            _previewPointVisible = true;
            _previewLine = lineNumber;
        }

        return false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return TextView != null ? new Size(TextView.DefaultLineHeight, 0) : new Size(0, 0);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _previewLine = -1;
        _previewPointVisible = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _previewPointVisible = true;
        var textView = TextView;

        var offset = _editor.GetOffsetFromPoint(e.GetPosition(this));

        if (offset != -1)
        {
            var lineClicked = textView.Document.GetLineByOffset(offset).LineNumber;
            var currentBreakPoint = _manager.FirstOrDefault(bp => bp.Line == lineClicked);

            if (currentBreakPoint != null)
            {
                _previewLine = lineClicked;
                _manager.Remove(currentBreakPoint);
            }
            else
            {
                _previewLine = -1;
                _manager.Add(lineClicked);
            }
        }

        InvalidateVisual();
        e.Handled = true;
    }

    public void Dispose()
    {
        _manager = null;
        _editor = null;
    }
}
