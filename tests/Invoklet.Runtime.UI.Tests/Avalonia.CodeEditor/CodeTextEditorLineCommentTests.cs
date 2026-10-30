using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Services.Preferences;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeEditor;
// Toggle Line Comment end to end: the configured key through Avalonia's input pipeline into a real
// editor, one undo step, and the cases where the key must not change anything.
public class CodeTextEditorLineCommentTests
{
    private static (Window Window, CodeTextEditor Editor, TextBox Other) Show(string text, string? prefix = "//")
    {
        var editor = new CodeTextEditor { Text = text, LineCommentPrefix = prefix, Height = 300 };
        new KeyboardSettings().ApplyTo(editor);
        var other = new TextBox();
        var panel = new StackPanel { Children = { other, editor } };
        var window = new Window { Width = 800, Height = 400, Content = panel };
        window.Show();
        editor.TextArea.Focus();
        return (window, editor, other);
    }

    private static RawInputModifiers Command => new KeyboardSettings().IsMac ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public void TheKey_Should_ToggleTheSelectedLines_AsOneUndoStep()
    {
        var (window, editor, _) = Show("a();\r\nb();\r\nc();");
        try
        {
            editor.Select(1, 7); // from inside "a();" to inside "b();"
            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("// a();\r\n// b();\r\nc();", editor.Text);

            editor.Undo();
            Assert.Equal("a();\r\nb();\r\nc();", editor.Text);
            editor.Redo();
            Assert.Equal("// a();\r\n// b();\r\nc();", editor.Text);

            editor.Select(0, editor.Text.IndexOf("c();", StringComparison.Ordinal));
            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("a();\r\nb();\r\nc();", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheKey_Should_ToggleTheCaretLine_AndKeepTheCaretOnIt()
    {
        var (window, editor, _) = Show("a();\nb();");
        try
        {
            editor.CaretOffset = 7; // inside "b();"
            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("a();\n// b();", editor.Text);
            Assert.Equal(2, editor.TextArea.Caret.Line);
            Assert.True(editor.TextArea.Selection.IsEmpty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheKey_Should_ChangeNothing_WithoutFocusInTheEditor()
    {
        var (window, editor, other) = Show("a();");
        try
        {
            other.Focus();
            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("a();", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheKey_Should_ChangeNothing_WithoutAPrefixOrInAReadOnlyEditor()
    {
        var (window, editor, _) = Show("a();", prefix: null);
        try
        {
            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("a();", editor.Text);

            editor.LineCommentPrefix = "//";
            editor.IsReadOnly = true;
            Assert.False(editor.ToggleLineComment());
            Assert.Equal("a();", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AReboundKey_Should_ReplaceTheDefault()
    {
        var (window, editor, _) = Show("a();");
        try
        {
            var keyboard = new KeyboardSettings();
            keyboard.SetKey(KeyCommands.ToggleLineComment, "F7");
            keyboard.ApplyTo(editor);

            window.KeyPressQwerty(PhysicalKey.Slash, Command);
            Assert.Equal("a();", editor.Text);
            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.None);
            Assert.Equal("// a();", editor.Text);
        }
        finally { window.Close(); }
    }
}
