// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace AvaloniaEdit.CodeCompletion;

/// <summary>
/// The code completion window.
/// </summary>
public class CompletionWindow : CompletionWindowBase
{
    private PopupWithCustomPosition _toolTip;
    private CompletionTipContentControl _toolTipContent;

    /// <summary>
    /// Gets the completion list used in this completion window.
    /// </summary>
    public CompletionList CompletionList { get; }

    /// <summary>
    /// Optional hook a host can set to extend the replacement range on a Tab-triggered commit:
    /// given the offset EndOffset would otherwise use, returns the end of whatever word/identifier
    /// occupies that position (walking forward from it), so committing mid-word consumes any
    /// pre-existing remainder instead of leaving it dangling after the inserted text (e.g. accepting
    /// "Console" with the caret at "Cons|ole" should produce "Console", not "Consoleole"). Left null,
    /// Tab behaves exactly like Enter/any other commit; kept as a delegate rather
    /// than a hardcoded identifier-character set to stay language-agnostic here.
    /// </summary>
    public Func<int, int> WordEndOffsetProvider { get; set; }

    /// <summary>
    /// Creates a new code completion window.
    /// </summary>
    public CompletionWindow(TextArea textArea) : base(textArea)
    {
        CompletionList = new CompletionList
        {
            CompletionAcceptAction = textArea.Options.CompletionAcceptAction
        };

        // keep height automatic
        CloseAutomatically = true;
        MaxHeight = 225;
        MaxWidth = 550;
        Child = CompletionList;
        // prevent user from resizing window to 0x0
        MinHeight = 15;
        MinWidth = 175;

        _toolTipContent = new CompletionTipContentControl();

        _toolTip = new PopupWithCustomPosition
        {
            // The popup should not interfere with the pointer input. Popup visibility is
            // controlled explicitly.
            IsLightDismissEnabled = false,

            PlacementTarget = this,
            Child = _toolTipContent,
        };

        LogicalChildren.Add(_toolTip);

        //_toolTip.Closed += (o, e) => ((Popup)o).Child = null;

        AttachEvents();
    }

    protected override void OnClosed()
    {
        base.OnClosed();

        if (_toolTip != null)
        {
            _toolTip.IsOpen = false;
            _toolTip = null;
            _toolTipContent = null;
        }
    }

    #region ToolTip handling

    private void CompletionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_toolTipContent == null) return;

        var item = CompletionList.SelectedItem;
        var description = item?.Description;


        if (description != null && TopLevel.GetTopLevel(CompletionList) is Control placementTarget && CompletionList.CurrentList != null)
        {
            _toolTipContent.Content = description;

            double yOffset = 0;
            var selectedIndex = CompletionList.ListBox.SelectedIndex;

            var itemContainer = CompletionList.ListBox.ContainerFromIndex(selectedIndex);

            if (itemContainer != null)
            {
                _toolTip.Placement = PlacementMode.RightEdgeAlignedTop;
                var position = itemContainer.TranslatePoint(new Point(0, 0), placementTarget);
                if (position.HasValue) yOffset = position.Value.Y;
            }
            else
            {
                //When scrolling down the container is not always ready
                //If that happens we align the tooltip at the bottom or top
                if (CompletionList.ListBox.FirstVisibleItem < selectedIndex)
                {
                    _toolTip.Placement = PlacementMode.RightEdgeAlignedBottom;
                }
                else
                {
                    _toolTip.Placement = PlacementMode.RightEdgeAlignedTop;
                }
            }

            _toolTip.Offset = new Point(2, yOffset);
            _toolTip.PlacementTarget = placementTarget;
            _toolTip.IsOpen = true;
        }
        else
        {
            _toolTip.IsOpen = false;
        }
    }

    #endregion

    private void CompletionList_InsertionRequested(object sender, EventArgs e)
    {
        // A typed commit CHARACTER (not Enter/Tab/click) shouldn't accept a match
        // the completion engine isn't confident about (CompletionList.IsSoftSelected) — only a
        // substring/camelCase guess, not something the user actually typed a real prefix of.
        // Tab/Enter/click are explicit accept gestures and always commit regardless of this (same
        // "check the concrete EventArgs type" technique as WordEndOffsetProvider's Tab check
        // below) — this only ever declines a TextInputEventArgs-triggered request, leaving the
        // window open and the just-typed character to insert normally, so the list keeps refining
        // as the user keeps typing instead of committing a guess out from under them.
        if (e is TextInputEventArgs && CompletionList.IsSoftSelected)
            return;

        Hide();
        // The window must close before Complete() is called.
        // If the Complete callback pushes stacked input handlers, we don't want to pop those when the CC window closes.
        var item = CompletionList.SelectedItem;

        var endOffset = EndOffset;
        if (e is KeyEventArgs { Key: Key.Tab } && WordEndOffsetProvider is { } getWordEnd)
            endOffset = Math.Max(endOffset, getWordEnd(endOffset));

        item?.Complete(TextArea, new AnchorSegment(TextArea.Document, StartOffset, endOffset - StartOffset), e);
    }

    private void CompletionList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        MinWidth = Math.Max(MinWidth, CompletionList.Bounds.Width);
    }

    private void AttachEvents()
    {
        CompletionList.InsertionRequested += CompletionList_InsertionRequested;
        CompletionList.SelectionChanged += CompletionList_SelectionChanged;
        CompletionList.SizeChanged += CompletionList_SizeChanged;
        TextArea.Caret.PositionChanged += CaretPositionChanged;
        TextArea.PointerWheelChanged += TextArea_MouseWheel;
        TextArea.TextInput += TextArea_PreviewTextInput;
    }

    /// <inheritdoc/>
    protected override void DetachEvents()
    {
        CompletionList.InsertionRequested -= CompletionList_InsertionRequested;
        CompletionList.SelectionChanged -= CompletionList_SelectionChanged;
        CompletionList.SizeChanged -= CompletionList_SizeChanged;
        TextArea.Caret.PositionChanged -= CaretPositionChanged;
        TextArea.PointerWheelChanged -= TextArea_MouseWheel;
        TextArea.TextInput -= TextArea_PreviewTextInput;
        base.DetachEvents();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled)
        {
            CompletionList.HandleKey(e);
        }
    }

    private void TextArea_PreviewTextInput(object sender, TextInputEventArgs e)
    {
        e.Handled = RaiseEventPair(this, null, TextInputEvent,
                                   new TextInputEventArgs { Text = e.Text });
    }

    private void TextArea_MouseWheel(object sender, PointerWheelEventArgs e)
    {
        e.Handled = RaiseEventPair(GetScrollEventTarget(),
                                   null, PointerWheelChangedEvent, e);
    }

    private Control GetScrollEventTarget()
    {
        if (CompletionList == null)
            return this;
        return CompletionList.ScrollViewer ?? CompletionList.ListBox ?? (Control)CompletionList;
    }

    /// <summary>
    /// Gets/Sets whether the completion window should close automatically.
    /// The default value is true.
    /// </summary>
    public bool CloseAutomatically { get; set; }

    /// <inheritdoc/>
    protected override bool CloseOnFocusLost => CloseAutomatically;

    /// <summary>
    /// When this flag is set, code completion closes if the caret moves to the
    /// beginning of the allowed range. This is useful in Ctrl+Space and "complete when typing",
    /// but not in dot-completion.
    /// Has no effect if CloseAutomatically is false.
    /// </summary>
    public bool CloseWhenCaretAtBeginning { get; set; }

    private void CaretPositionChanged(object sender, EventArgs e)
    {
        var offset = TextArea.Caret.Offset;
        if (offset == StartOffset)
        {
            if (CloseAutomatically && CloseWhenCaretAtBeginning)
            {
                Hide();
            }
            else
            {
                CompletionList.SelectItem(string.Empty);

                if (CompletionList.ListBox.ItemCount == 0)
                {
                    if (CloseAutomatically) { Hide(); return; }
                    CompletionList.IsVisible = false;
                }
                else CompletionList.IsVisible = true;
            }
            return;
        }
        if (offset < StartOffset || offset > EndOffset)
        {
            if (CloseAutomatically)
            {
                Hide();
            }
        }
        else
        {
            var document = TextArea.Document;
            if (document != null)
            {
                CompletionList.SelectItem(document.GetText(StartOffset, offset - StartOffset));

                if (CompletionList.ListBox.ItemCount == 0)
                {
                    if (CloseAutomatically) { Hide(); return; }
                    CompletionList.IsVisible = false;
                }
                else CompletionList.IsVisible = true;
            }
        }
    }
}
