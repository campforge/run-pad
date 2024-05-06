using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Utils;
using DotPad.Avalonia.CodeTextEditor.Shared;

namespace DotPad.Avalonia.CodeTextEditor;

public class CodeTextEditor : TextEditor
{
    // private readonly TextMarkerService _textMarkerService;
    // private ContextActionsRenderer? _contextActionsRenderer;

    private BreakPointMargin _breakpointMargin;

    protected override Type StyleKeyOverride => typeof(TextEditor);


    // public static readonly StyledProperty<IImage> ContextActionsIconProperty =
    //     CommonProperty.Register<CodeTextEditor, IImage>(
    //         nameof(ContextActionsIcon), onChanged: OnContextActionsIconChanged);

    // public IImage ContextActionsIcon
    // {
    //     get => GetValue(ContextActionsIconProperty);
    //     set => SetValue(ContextActionsIconProperty, value);
    // }

    public CodeTextEditor()
    {
        // _textMarkerService = new TextMarkerService(this);
        //TextArea.TextView.BackgroundRenderers.Add(_textMarkerService);

        //TextArea.TextView.LineTransformers.Add(_textMarkerService);


        // _contextActionsRenderer = new ContextActionsRenderer(this, _textMarkerService) { IconImage = ContextActionsIcon };
        //_contextActionsRenderer.Providers.Add(new ContextActionProvider());

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
            .FirstOrDefault(x =>
                x.Command == AvaloniaEditCommands.DeleteLine);

        if (deleteLineCommand != null)
        {
            commandBindings.Remove(deleteLineCommand);
        }
    }

    private void InitContextMenu()
    {
        ContextMenu = new ContextMenu
        {
            ItemsSource = new List<MenuItem>
            {
                new() { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) },
                new() { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) },
                new() { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) }
            }
        };
    }

    private void Initialize()
    {
    }

    public static readonly RoutedEvent ToolTipRequestEvent =
        CommonEvent.Register<CodeTextEditor, ToolTipRequestEventArgs>(
            nameof(ToolTipRequest), RoutingStrategies.Bubble);

    public event EventHandler<ToolTipRequestEventArgs> ToolTipRequest
    {
        add => AddHandler(ToolTipRequestEvent, value);
        remove => RemoveHandler(ToolTipRequestEvent, value);
    }

    /// <summary>
    /// ShowLineNumbers dependency property.
    /// </summary>
    public static readonly StyledProperty<bool> ShowBreakpointsProperty =
        AvaloniaProperty.Register<TextEditor, bool>("ShowBreakpoints");

    /// <summary>
    /// Specifies whether line numbers are shown on the left to the text view.
    /// </summary>
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
        {
            leftMargins.Insert(1, _breakpointMargin);
        }
        else
        {
            leftMargins.Remove(_breakpointMargin);
        }
    }

    // private static void OnContextActionsIconChanged(CodeTextEditor editor, CommonPropertyChangedArgs<IImage> args)
    // {
    //     if (editor._contextActionsRenderer != null)
    //     {
    //         editor._contextActionsRenderer.IconImage = args.NewValue;
    //     }
    // }

    public int GetOffsetFromPoint(Point point)
    {
        var position = GetPositionFromPoint(point);

        var offset = position != null ? Document.GetOffset(position.Value.Location) : -1;

        return offset;
    }
}

public sealed class ContextActionProvider : IContextActionProvider
{
    public Task<IEnumerable<object>> GetActions(int offset, int length, CancellationToken cancellationToken)
    {
        return Task.FromResult(Enumerable.Empty<object>());
       // throw new NotImplementedException();
    }

    public ICommand GetActionCommand(object action)
    {
        throw new NotImplementedException();
    }
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
        var result = new Point(x, y);
        return result;
    }
    
    private  void  CreatePoint(
        DrawingContext context, 
        VisualLine line,
        double height,
        IBrush brush)
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
            var visualLine =
                TextView.VisualLines
                    .FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == breakPoint.Line);
            if (visualLine != null)
            {
                CreatePoint(context, visualLine, height, Brush.Parse("#FF3737"));
            }
        }

        if (!_previewPointVisible && _previewLine >= 0) 
            return;
        {
            var visualLine =
                TextView.VisualLines.FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == _previewLine);

            if (visualLine != null)
            {
                CreatePoint(context, visualLine, height, Brush.Parse("#E67466"));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {

        Console.WriteLine(e.GetPosition(_editor));

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
        {
            _previewPointVisible = false;
        }
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
            var lineClicked = -1;
            lineClicked = textView.Document.GetLineByOffset(offset).LineNumber; // convert from text line to visual line.

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