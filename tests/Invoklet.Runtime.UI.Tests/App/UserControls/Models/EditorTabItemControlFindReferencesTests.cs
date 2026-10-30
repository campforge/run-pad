using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;
using Invoklet.UserControls;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls.Models;

// EditorsTabItemControl.BuildFindReferencesMenu builds
// the clickable results list from the same IDocumentHighlightSpan data the highlight-occurrences
// feature already computes (see CSharpIntelliSenseDocumentHighlightsTests.cs for symbol
// resolution); internal specifically so these can inspect/click the built menu directly, the same
// rationale as EditorTabsControl.BuildAllTabsMenu (headless Avalonia has no reliable way to drive
// an actual popup open/click interaction).
public class EditorTabItemControlFindReferencesTests
{
    private sealed record TestSpan(int Start, int Length) : IDocumentHighlightSpan;

    private static (Window Window, CodeTextEditor Editor) CreateEditor(string text)
    {
        var editor = new CodeTextEditor { Document = new TextDocument(text) };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        return (window, editor);
    }

    [AvaloniaFact]
    public void BuildFindReferencesMenu_Should_ReturnNull_WhenThereAreNoHighlights()
    {
        var (_, editor) = CreateEditor("value");

        var menu = EditorsTabItemControl.BuildFindReferencesMenu([], editor);

        Assert.Null(menu);
    }

    [AvaloniaFact]
    public void BuildFindReferencesMenu_Should_ListOneItemPerOccurrence_WithItsLineNumberAndText()
    {
        var (_, editor) = CreateEditor("value = 1;\nConsole.WriteLine(value);");
        var secondOccurrence = editor.Document.Text.IndexOf("value)", StringComparison.Ordinal);
        IReadOnlyList<IDocumentHighlightSpan> highlights = [new TestSpan(0, 5), new TestSpan(secondOccurrence, 5)];

        var menu = EditorsTabItemControl.BuildFindReferencesMenu(highlights, editor);

        Assert.NotNull(menu);
        var items = menu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Line 1: value = 1;", ((TextBlock)items[0].Header!).Text);
        Assert.Equal("Line 2: Console.WriteLine(value);", ((TextBlock)items[1].Header!).Text);
    }

    [AvaloniaFact]
    public void ClickingAnItem_Should_MoveTheCaret_ToThatOccurrence()
    {
        var (_, editor) = CreateEditor("value = 1;\nConsole.WriteLine(value);");
        var secondOccurrence = editor.Document.Text.IndexOf("value)", StringComparison.Ordinal);
        IReadOnlyList<IDocumentHighlightSpan> highlights = [new TestSpan(0, 5), new TestSpan(secondOccurrence, 5)];
        var menu = EditorsTabItemControl.BuildFindReferencesMenu(highlights, editor);
        var secondItem = (MenuItem)menu!.Items[1]!;

        secondItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(secondOccurrence, editor.TextArea.Caret.Offset);
    }
}
