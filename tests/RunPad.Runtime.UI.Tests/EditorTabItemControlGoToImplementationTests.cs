using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Contracts.IntelliSense;
using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// EditorsTabItemControl.BuildGoToImplementationMenu,
// the picker shown when a symbol has more than one implementation/override in the script (see
// CSharpIntelliSenseGoToImplementationTests.cs for symbol resolution) — internal specifically so
// these can inspect/click the built menu directly, same rationale as BuildFindReferencesMenu.
public class EditorTabItemControlGoToImplementationTests
{
    private sealed record TestImplementation(int Offset, string DisplayName) : IImplementationLocation;

    private static (Window Window, CodeTextEditor Editor) CreateEditor(string text)
    {
        var editor = new CodeTextEditor { Document = new TextDocument(text) };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        return (window, editor);
    }

    [AvaloniaFact]
    public void BuildGoToImplementationMenu_Should_ReturnNull_WhenThereAreNoImplementations()
    {
        var (_, editor) = CreateEditor("interface IShape { }");

        var menu = EditorsTabItemControl.BuildGoToImplementationMenu([], editor);

        Assert.Null(menu);
    }

    [AvaloniaFact]
    public void BuildGoToImplementationMenu_Should_ListOneItemPerImplementation_WithItsDisplayName()
    {
        var (_, editor) = CreateEditor("class Circle : IShape { }\nclass Square : IShape { }");
        var squareOffset = editor.Document.Text.IndexOf("Square", StringComparison.Ordinal);
        IReadOnlyList<IImplementationLocation> implementations =
            [new TestImplementation(0, "Circle"), new TestImplementation(squareOffset, "Square")];

        var menu = EditorsTabItemControl.BuildGoToImplementationMenu(implementations, editor);

        Assert.NotNull(menu);
        var items = menu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Circle", items[0].Header);
        Assert.Equal("Square", items[1].Header);
    }

    [AvaloniaFact]
    public void ClickingAnItem_Should_MoveTheCaret_ToThatImplementation()
    {
        var (_, editor) = CreateEditor("class Circle : IShape { }\nclass Square : IShape { }");
        var squareOffset = editor.Document.Text.IndexOf("Square", StringComparison.Ordinal);
        IReadOnlyList<IImplementationLocation> implementations =
            [new TestImplementation(0, "Circle"), new TestImplementation(squareOffset, "Square")];
        var menu = EditorsTabItemControl.BuildGoToImplementationMenu(implementations, editor);
        var secondItem = (MenuItem)menu!.Items[1]!;

        secondItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(squareOffset, editor.TextArea.Caret.Offset);
    }
}
