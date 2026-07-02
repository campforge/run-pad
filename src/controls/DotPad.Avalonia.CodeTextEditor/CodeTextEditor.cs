using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using static AvaloniaEdit.TextMate.TextMate;

namespace DotPad.Avalonia.CodeTextEditor;

public class CodeTextEditor : TextEditor
{
    public static readonly StyledProperty<bool> ShowBreakpointsProperty =
        AvaloniaProperty.Register<CodeTextEditor, bool>(nameof(ShowBreakpoints));

    public static readonly StyledProperty<bool> ShowFoldingProperty =
        AvaloniaProperty.Register<CodeTextEditor, bool>(nameof(ShowFolding));

    static CodeTextEditor()
    {
        ShowBreakpointsProperty.Changed.AddClassHandler<CodeTextEditor>(
            static (editor, e) => editor.OnShowBreakpointsChanged((bool?)e.NewValue));
        ShowFoldingProperty.Changed.AddClassHandler<CodeTextEditor>(
            static (editor, e) => editor.OnShowFoldingChanged((bool?)e.NewValue));
    }

    private readonly BreakpointStore _breakpointStore;
    private readonly BreakPointMargin _breakpointMargin;
    private readonly DebugLineHighlighter _debugLineHighlighter;
    private readonly BraceFoldingStrategy _foldingStrategy = new();
    private readonly DispatcherTimer _foldingTimer;
    private FoldingManager?       _foldingManager;
    private VsCodeFoldingMargin?  _foldingMargin;
    private bool _isLoaded;
    private CodeTextEditorOptions _editorOptions = new();
    private RegistryOptions? _registryOptions;
    private Installation? _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;

    public event EventHandler<BreakpointEventArgs>? BreakpointAdded;
    public event EventHandler<BreakpointEventArgs>? BreakpointRemoved;

    /// <summary>Sync completion provider. For async (e.g. Roslyn), use <see cref="AsyncCompletionProvider"/> instead.</summary>
    public event EventHandler<CompletionRequestedEventArgs>? CompletionRequested;

    /// <summary>Sync overload provider. For async use, see <see cref="AsyncOverloadProvider"/>.</summary>
    public event EventHandler<OverloadRequestedEventArgs>? OverloadRequested;

    /// <summary>Async completion provider — called instead of <see cref="CompletionRequested"/> when set.</summary>
    public Func<CompletionRequestedEventArgs, Task>? AsyncCompletionProvider { get; set; }

    /// <summary>Async overload provider — called instead of <see cref="OverloadRequested"/> when set.</summary>
    public Func<OverloadRequestedEventArgs, Task>? AsyncOverloadProvider { get; set; }

    public IEnumerable<IBreakpoint> Breakpoints => _breakpointStore;

    public ElementGenerator InlineControls { get; } = new();

    /// <summary>Characters that open the completion dropdown.</summary>
    public HashSet<char> CompletionTriggers { get; } = [];

    /// <summary>Characters that open the overload insight window.</summary>
    public HashSet<char> OverloadTriggers { get; } = [];

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

    public bool ShowFolding
    {
        get => GetValue(ShowFoldingProperty);
        set => SetValue(ShowFoldingProperty, value);
    }

    public int? DebugCurrentLine
    {
        get => _breakpointMargin.DebugLine;
        set
        {
            _breakpointMargin.DebugLine        = value;
            _debugLineHighlighter.CurrentLine  = value;
            _breakpointMargin.InvalidateVisual();
            TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            if (value is { } line)
                ScrollToLine(line);
        }
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public CodeTextEditor()
    {
        _breakpointStore = new BreakpointStore();
        _breakpointStore.BreakpointAdded   += (_, e) => BreakpointAdded?.Invoke(this, e);
        _breakpointStore.BreakpointRemoved += (_, e) => BreakpointRemoved?.Invoke(this, e);

        _breakpointMargin     = new BreakPointMargin(this, _breakpointStore);
        _debugLineHighlighter = new DebugLineHighlighter();

        _foldingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _foldingTimer.Tick += (_, _) => { _foldingTimer.Stop(); UpdateFoldings(); };

        // Remove DeleteLine — conflicts with our line-based workflow
        var deleteLineCommand = TextArea.CommandBindings
            .OfType<RoutedCommandBinding>()
            .FirstOrDefault(x => x.Command == AvaloniaEditCommands.DeleteLine);
        if (deleteLineCommand != null)
            TextArea.CommandBindings.Remove(deleteLineCommand);

        ContextMenu = new ContextMenu
        {
            ItemsSource = new MenuItem[]
            {
                new() { Header = "Copy",  Command = ApplicationCommands.Copy  },
                new() { Header = "Paste", Command = ApplicationCommands.Paste },
                new() { Header = "Cut",   Command = ApplicationCommands.Cut   },
            }
        };

        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        ShowLineNumbers = true;

        TextArea.TextView.BackgroundRenderers.Add(_debugLineHighlighter);

        Options.AllowToggleOverstrikeMode = true;
        Options.ShowBoxForControlCharacters = true;
        TextArea.RightClickMovesCaret = true;

        TextArea.TextView.LineTransformers.Add(new UnderlineAndStrikeThroughTransformer());
        TextArea.TextView.ElementGenerators.Add(InlineControls);

        TextArea.TextEntering += OnTextEntering;
        TextArea.TextEntered  += OnTextEntered;

        TextChanged += (_, _) => { _foldingTimer.Stop(); _foldingTimer.Start(); };

        // FoldingManager.Install requires the document to be in its final state;
        // defer until Loaded so AvaloniaEdit's template has fully applied.
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            if (ShowFolding && _foldingManager is null)
                InstallFolding();
        };

        // Reinstall when the document object itself is replaced (new file opened).
        DocumentChanged += (_, _) =>
        {
            if (!ShowFolding || !_isLoaded) return;
            if (_foldingManager is not null)
                UninstallFolding();
            InstallFolding();
        };

        AddHandler(PointerWheelChangedEvent, OnFontSizeScroll, RoutingStrategies.Bubble, handledEventsToo: true);

        ApplyOptions();
    }

    private void ApplyOptions()
    {
        ShowBreakpoints = _editorOptions.ShowBreakpoints;
        ShowFolding     = _editorOptions.ShowFolding;
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

    private void OnShowFoldingChanged(bool? show)
    {
        if (show == true)
        {
            if (_isLoaded && _foldingManager is null)
                InstallFolding();
            // else: deferred to Loaded event
        }
        else if (_foldingManager is not null)
        {
            UninstallFolding();
        }
    }

    private void InstallFolding()
    {
        _foldingManager = FoldingManager.Install(TextArea);

        // Replace AvaloniaEdit's default FoldingMargin with our VS Code-style one.
        // FoldingManager.Install adds a FoldingMargin; we swap it out immediately.
        var defaultMargin = TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (defaultMargin is not null)
        {
            var idx = TextArea.LeftMargins.IndexOf(defaultMargin);
            TextArea.LeftMargins.Remove(defaultMargin);
            _foldingMargin = new VsCodeFoldingMargin(_foldingManager);
            TextArea.LeftMargins.Insert(idx, _foldingMargin);
        }

        UpdateFoldings();
    }

    private void UninstallFolding()
    {
        if (_foldingMargin is not null)
        {
            TextArea.LeftMargins.Remove(_foldingMargin);
            _foldingMargin = null;
        }
        FoldingManager.Uninstall(_foldingManager!);
        _foldingManager = null;
    }

    private void UpdateFoldings()
    {
        if (_foldingManager is null || Document is null) return;
        _foldingStrategy.UpdateFoldings(_foldingManager, Document);
        _foldingMargin?.InvalidateVisual();
    }

    private void OnFontSizeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        FontSize = e.Delta.Y > 0 ? FontSize + 1 : Math.Max(1, FontSize - 1);
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } && _completionWindow != null && !char.IsLetterOrDigit(e.Text[0]))
            _completionWindow.CompletionList.RequestInsertion(e);

        _insightWindow?.Hide();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 }) return;
        var ch = e.Text[0];

        if (CompletionTriggers.Contains(ch))
            ShowCompletion(ch);
        else if (OverloadTriggers.Contains(ch))
            ShowOverloads(ch);
    }

    private async void ShowCompletion(char trigger)
    {
        var args = new CompletionRequestedEventArgs(trigger, TextArea.Caret.Offset);

        if (AsyncCompletionProvider != null)
            await AsyncCompletionProvider(args);
        else
            CompletionRequested?.Invoke(this, args);

        if (args.Items.Count == 0) return;

        _completionWindow = new CompletionWindow(TextArea);
        _completionWindow.Closed += (_, _) => _completionWindow = null;

        foreach (var item in args.Items)
            _completionWindow.CompletionList.CompletionData.Add(item);

        _completionWindow.Show();
    }

    private async void ShowOverloads(char trigger)
    {
        var args = new OverloadRequestedEventArgs(trigger, TextArea.Caret.Offset);

        if (AsyncOverloadProvider != null)
            await AsyncOverloadProvider(args);
        else
            OverloadRequested?.Invoke(this, args);

        if (args.Overloads.Count == 0) return;

        _insightWindow = new OverloadInsightWindow(TextArea);
        _insightWindow.Closed += (_, _) => _insightWindow = null;
        _insightWindow.Provider = new OverloadProvider(args.Overloads);
        _insightWindow.Show();
    }

    public int GetOffsetFromPoint(Point point)
    {
        var position = GetPositionFromPoint(point);
        return position != null ? Document.GetOffset(position.Value.Location) : -1;
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

    private sealed class OverloadProvider(IReadOnlyList<(string Header, string? Description)> items)
        : IOverloadProvider
    {
        private int _selectedIndex;

        public int Count => items.Count;

        public string CurrentIndexText => $"{_selectedIndex + 1} of {items.Count}";

        public object CurrentHeader => items[_selectedIndex].Header;

        public object? CurrentContent => items[_selectedIndex].Description;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
