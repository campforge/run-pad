using System.ComponentModel;
using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor;

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
