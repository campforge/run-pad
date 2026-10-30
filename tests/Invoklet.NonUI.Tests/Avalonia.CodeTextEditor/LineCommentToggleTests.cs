using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Services.Preferences;
using Xunit;

namespace Invoklet.NonUI.Tests.Avalonia.CodeTextEditor;

public class LineCommentToggleTests
{
    // '|' marks the caret, '[' and ']' the selection; the markers are removed before toggling.
    private static string Toggle(string marked, string prefix = "//")
    {
        var text = marked.Replace("|", "").Replace("[", "").Replace("]", "");
        int start, end;
        if (marked.Contains('|'))
        {
            start = end = marked.IndexOf('|');
        }
        else
        {
            start = marked.IndexOf('[');
            end = marked.IndexOf(']') - 1;
        }
        return LineCommentToggle.Apply(text, LineCommentToggle.Compute(text, start, end, prefix));
    }

    [Fact]
    public void Toggle_Should_CommentTheCaretLine_WhenNothingIsSelected()
    {
        Assert.Equal("a();\n// b();\nc();", Toggle("a();\nb|();\nc();"));
    }

    [Fact]
    public void Toggle_Should_UncommentTheCaretLine_WhenItIsAComment()
    {
        Assert.Equal("a();\nb();", Toggle("a();\n// b|();"));
        Assert.Equal("a();\nb();", Toggle("a();\n//b|();"));
    }

    [Fact]
    public void Toggle_Should_CommentEveryLineTheSelectionTouches_AfterEachLinesIndentation()
    {
        Assert.Equal(
            "void M()\n{\n    // a();\n    // if (x)\n        // b();\n}",
            Toggle("void M()\n{\n    a[();\n    if (x)\n        b(]);\n}"));
    }

    [Fact]
    public void Toggle_Should_NotTouchALastLineTheSelectionOnlyReachesAtItsStart()
    {
        Assert.Equal("// a();\n// b();\nc();", Toggle("[a();\nb();\n]c();"));
    }

    [Fact]
    public void Toggle_Should_TreatAReversedSelectionLikeAForwardOne()
    {
        const string text = "a();\nb();\nc();";
        var forward = LineCommentToggle.Compute(text, 1, 7, "//");
        var reversed = LineCommentToggle.Compute(text, 7, 1, "//");
        Assert.Equal(forward, reversed);
        Assert.Equal("// a();\n// b();\nc();", LineCommentToggle.Apply(text, forward));
    }

    [Fact]
    public void Toggle_Should_LeaveBlankLinesAlone()
    {
        Assert.Equal("// a();\n\n  \t\n// b();", Toggle("[a();\n\n  \t\nb();]"));
        Assert.Equal("a();\n\n  \t\nb();", Toggle("[// a();\n\n  \t\n// b();]"));
    }

    [Fact]
    public void Toggle_Should_DoNothing_OnABlankLineOrWithoutAPrefix()
    {
        Assert.Empty(LineCommentToggle.Compute("a();\n   \nb();", 6, 6, "//"));
        Assert.Empty(LineCommentToggle.Compute("a();", 1, 1, null));
        Assert.Empty(LineCommentToggle.Compute("a();", 1, 1, ""));
    }

    [Fact]
    public void Toggle_Should_CommentAllLines_WhenOnlySomeAreComments()
    {
        Assert.Equal("// // a();\n// b();", Toggle("[// a();\nb();]"));
    }

    [Fact]
    public void Toggle_Should_UncommentOneLevel_WhenAllLinesAreComments()
    {
        Assert.Equal("    a();\n    // b();", Toggle("[    // a();\n    // // b();]"));
    }

    [Fact]
    public void Toggle_Should_KeepEachLinesTerminator()
    {
        Assert.Equal("// a\r\n// b\n// c\rd", Toggle("[a\r\nb\nc\r]d"));
        Assert.Equal("a\r\nb\nc\rd", Toggle("[// a\r\n// b\n// c\r]d"));
    }

    [Fact]
    public void Toggle_Should_UseVisualBasicsPrefix()
    {
        Assert.Equal("' Dim a = 1\n' Console.WriteLine(a)", Toggle("[Dim a = 1\nConsole.WriteLine(a)]", "'"));
        Assert.Equal("Dim a = 1", Toggle("'Dim| a = 1", "'"));
    }

    [Fact]
    public void Toggle_Should_NotTakeAPartialPrefixForAComment()
    {
        Assert.Equal("// / a", Toggle("/ |a"));
    }

    [Theory]
    [InlineData(false, "Ctrl+/")]
    [InlineData(true, "Cmd+/")]
    public void ToggleLineComment_Should_HaveARiderLikeDefaultKey(bool mac, string key)
    {
        var keyboard = new KeyboardSettings { IsMac = mac };
        Assert.Equal(key, keyboard.KeyFor(KeyCommands.ToggleLineComment));
        Assert.Equal(global::Avalonia.Input.Key.OemQuestion, keyboard.GestureFor(KeyCommands.ToggleLineComment)!.Key);
        Assert.Contains(KeyCommands.ToggleLineComment, KeyCommands.All);
    }
}
