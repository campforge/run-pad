using Avalonia;
using Avalonia.Controls;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Services;

namespace DotPad.Windows;

/// <summary>
/// Non-modal "Go to Definition" (F12, TODO.md #62, user request 2026-07-19) viewer for a symbol
/// decompiled from outside the user's own script — mirrors dnSpy/ILSpy's own decompiled-tab view, as
/// a separate window rather than a tab (explicit user request: keep it out of the main tab strip so
/// the script keeps running/editable behind it).
/// </summary>
public partial class DecompiledViewWindow : Window
{
    /// <summary>Test-only observability — mirrors CodeTextEditor.HighlightedOccurrences' rationale.</summary>
    internal CodeTextEditor Editor => (CodeTextEditor)EditorHost.Content!;

    public DecompiledViewWindow(IThemeService themeService, string title, string code, int line)
    {
        InitializeComponent();
        Title = title;

        var editor = new CodeTextEditor
        {
            IsReadOnly = true,
            ShowLineNumberText = true,
            Text = code,
        };
        EditorHost.Content = editor;

        // Same reasoning as EditorTabItemControl's own onAttached dance: SetLanguage calls Redraw()
        // internally, which needs the editor already in the visual tree.
        EventHandler<VisualTreeAttachmentEventArgs>? onAttached = null;
        onAttached = (_, _) =>
        {
            editor.AttachedToVisualTree -= onAttached;
            editor.SetLanguage(themeService.CurrentTheme.Editor.TextMateName, ".cs");
            editor.TextArea.Caret.Line = line;
            editor.TextArea.Caret.Column = 1;
            editor.ScrollToLine(editor.TextArea.Caret.Line);
        };
        editor.AttachedToVisualTree += onAttached;
    }

    // XAML previewer only — mirrors EditorTabsControl's own empty ctor.
    public DecompiledViewWindow()
    {
        InitializeComponent();
    }
}
