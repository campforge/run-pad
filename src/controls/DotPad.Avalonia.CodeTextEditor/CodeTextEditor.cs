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
    public static readonly StyledProperty<bool> ShowBreakpointsProperty =
        AvaloniaProperty.Register<CodeTextEditor, bool>(nameof(ShowBreakpoints));

    static CodeTextEditor()
    {
        ShowBreakpointsProperty.Changed.AddClassHandler<CodeTextEditor>(
            static (editor, e) => editor.OnShowBreakpointsChanged((bool?)e.NewValue));
    }

    private readonly BreakPointMargin _breakpointMargin;
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

    public bool ShowBreakpoints
    {
        get => GetValue(ShowBreakpointsProperty);
        set => SetValue(ShowBreakpointsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public CodeTextEditor()
    {
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

    private void OnShowBreakpointsChanged(bool? showBreakpoints)
    {
        var leftMargins = TextArea.LeftMargins;
        if (showBreakpoints == true)
            leftMargins.Insert(1, _breakpointMargin);
        else
            leftMargins.Remove(_breakpointMargin);
    }

    private void OnFontSizeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Delta.Y > 0) FontSize++;
        else FontSize = FontSize > 1 ? FontSize - 1 : 1;
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
        if (e.Text is { Length: > 0 } && _completionWindow != null && !char.IsLetterOrDigit(e.Text[0]))
            _completionWindow.CompletionList.RequestInsertion(e);

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
