using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor;

public static class AvaloniaEditExtensions
{
    public static bool IsOpen(this CompletionWindowBase window) => window?.IsEffectivelyVisible == true;
}
