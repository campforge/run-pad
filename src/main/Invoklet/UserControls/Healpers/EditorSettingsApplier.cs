using Avalonia;
using Avalonia.Media;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Services.Preferences;

namespace Invoklet.UserControls.Healpers;

/// <summary>What the editor settings (and the theme's selection colour) do to a code editor control, for the tab and for the plain text
/// editor window alike.</summary>
internal static class EditorSettingsApplier
{
    // The editor's own font size: the configured size times the code zoom. The interface scale is
    // not part of it — the window's layout transform already applies that to everything inside.
    internal static double CodeFontSize(EditorSettings s, double codeZoom) => Math.Max(1, s.FontSize * codeZoom);

    internal static void ApplySelectionColor(CodeTextEditor editor)
    {
        // TextMate may set SelectionBrush to a dark color even for light themes.
        // Read the brush from the current theme so it's fully configurable per theme.
        var variant = Application.Current?.ActualThemeVariant;
        if (Application.Current?.TryGetResource("Invoklet.Editor.SelectionBrush", variant, out var res) == true
            && res is IBrush brush)
            editor.TextArea.SelectionBrush = brush;
        editor.TextArea.SelectionForeground = null;
    }

    // Test-only observability (InternalsVisibleTo), same "internal test hook" pattern used elsewhere
    // in the solution — lets a test confirm settings (notably FontFamily) actually reach
    // the real CodeTextEditor without needing to construct a whole EditorTabItemControl.
    internal static void ApplyEditorSettings(CodeTextEditor editor, EditorSettings s, double codeZoom = 1)
    {
        editor.ShowLineNumberText = s.ShowLineNumbers;
        editor.ShowFolding = s.ShowFolding;
        editor.FontSize = CodeFontSize(s, codeZoom);
        editor.FontFamily = MonospaceFont.Resolve(s.FontFamily);
        editor.WordWrap = s.WordWrap;
        editor.Options.ConvertTabsToSpaces = !s.UseTabs;
        editor.Options.IndentationSize = s.IndentationSize;
        if (editor.TextArea.IndentationStrategy is AvaloniaEdit.Indentation.CSharp.CSharpIndentationStrategy strategy)
            strategy.IndentationString = editor.Options.IndentationString;
        editor.Options.ShowTabs = s.ShowTabs;
        editor.Options.ShowSpaces = s.ShowSpaces;
        editor.Options.ShowEndOfLine = s.ShowEndOfLine;
        editor.Options.ColumnRulerPositions = s.ShowColumnRuler
            ? [s.ColumnRulerPosition]
            : [];
        editor.CommitCharactersEnabled = s.CommitCharactersEnabled;
    }
}
