
using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor.Shared;

public interface ICompletionDataEx : ICompletionData
{
    bool IsSelected { get; }

    string SortText { get; }
}

public interface IOverloadProviderEx : IOverloadProvider
{
    void Refresh();
}
