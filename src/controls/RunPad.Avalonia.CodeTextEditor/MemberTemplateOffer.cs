using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace RunPad.Avalonia.CodeTextEditor;

/// <summary>
/// The hint shown at the caret while a member template is on offer (see
/// <see cref="CodeTextEditor.MemberTemplateSnippet"/>). It only listens: Tab accepts the offer,
/// Escape dismisses it, and any other key dismisses it and then does what it always does.
/// </summary>
internal sealed class MemberTemplateOffer : TextAreaStackedInputHandler
{
    private readonly CodeTextEditor _editor;
    private readonly Popup _popup;

    public MemberTemplateOffer(CodeTextEditor editor, string snippet, string title)
        : base(editor.TextArea)
    {
        _editor = editor;
        Snippet = snippet;
        Hint = new TextBlock
        {
            Text = $"{title} (Tab)",
            FontSize = Math.Max(8, editor.FontSize * 0.85),
            Foreground = editor.Foreground,
        };
        _popup = new Popup
        {
            // Placed like the completion window, just below the caret's line.
            PlacementTarget = editor.TextArea.TextView,
            InheritsTransform = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = false,
            Focusable = false,
            Child = new Border
            {
                Background = editor.Background ?? Brushes.White,
                BorderBrush = editor.Foreground ?? Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                Child = Hint,
            },
        };
    }

    /// <summary>The name of the snippet in <see cref="CodeTextEditor.Snippets"/> on offer.</summary>
    public string Snippet { get; }

    internal TextBlock Hint { get; }

    internal bool IsOpen => _popup.IsOpen;

    public void Show()
    {
        var textView = TextArea.TextView;
        var position = textView.GetVisualPosition(TextArea.Caret.Position, VisualYPosition.LineBottom) - textView.ScrollOffset;
        _popup.HorizontalOffset = position.X;
        _popup.VerticalOffset = position.Y;
        ((ISetLogicalParent)_popup).SetParent(TopLevel.GetTopLevel(TextArea));
        _popup.IsOpen = true;
    }

    public void Hide()
    {
        _popup.IsOpen = false;
        ((ISetLogicalParent)_popup).SetParent(null);
    }

    public override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _editor.AcceptMemberTemplateOffer();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _editor.DismissMemberTemplateOffer();
        }
        else if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            _editor.DismissMemberTemplateOffer();
        }
    }
}
