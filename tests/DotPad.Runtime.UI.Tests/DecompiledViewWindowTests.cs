using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Services;
using DotPad.Windows;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19) — the non-modal window
// EditorTabItemControl opens for a symbol decompiled from outside the user's own script (explicit
// user request: a separate window, not another tab in the main strip).
public class DecompiledViewWindowTests
{
    private static Mock<IThemeService> CreateThemeService()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        return theme;
    }

    [AvaloniaFact]
    public void Should_ShowReadOnlyDecompiledText_WithTitleAndCaretOnTheRequestedLine()
    {
        const string code = "class Foo\n{\n    void Bar() { }\n}\n";
        var window = new DecompiledViewWindow(CreateThemeService().Object, "Foo.Bar()", code, line: 3);
        window.Show();

        Assert.Equal("Foo.Bar()", window.Title);
        Assert.True(window.Editor.IsReadOnly);
        Assert.Equal(code, window.Editor.Text);
        Assert.Equal(3, window.Editor.TextArea.Caret.Line);
    }
}
