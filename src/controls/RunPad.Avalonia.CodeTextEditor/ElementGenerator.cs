using Avalonia.Controls;
using AvaloniaEdit.Rendering;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class ElementGenerator : VisualLineElementGenerator, IComparer<KeyValuePair<int, Control>>
{
    private readonly List<KeyValuePair<int, Control>> _controls = new();

    public override int GetFirstInterestedOffset(int startOffset)
    {
        int pos = _controls.BinarySearch(new KeyValuePair<int, Control>(startOffset, null!), this);
        if (pos < 0) pos = ~pos;
        return pos < _controls.Count ? _controls[pos].Key : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        int pos = _controls.BinarySearch(new KeyValuePair<int, Control>(offset, null!), this);
        return pos >= 0 ? new InlineObjectElement(0, _controls[pos].Value) : null;
    }

    int IComparer<KeyValuePair<int, Control>>.Compare(KeyValuePair<int, Control> x, KeyValuePair<int, Control> y)
        => x.Key.CompareTo(y.Key);
}
