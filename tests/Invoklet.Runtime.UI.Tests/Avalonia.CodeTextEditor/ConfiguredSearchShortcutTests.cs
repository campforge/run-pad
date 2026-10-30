using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using Invoklet.Services.Preferences;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeTextEditor;

public class ConfiguredSearchShortcutTests
{
    [AvaloniaFact]
    public void SearchKeys_Should_RebindUnbindAndUpdateHints_WithoutChangingOtherEditors()
    {
        var editor = new Invoklet.Avalonia.CodeTextEditor.CodeTextEditor { Text = "one two one" };
        var other = new Invoklet.Avalonia.CodeTextEditor.CodeTextEditor();
        var keyboard = new KeyboardSettings();
        keyboard.SetKey(KeyCommands.FindText, "F7");
        keyboard.SetKey(KeyCommands.ReplaceText, "F8");
        keyboard.SetKey(KeyCommands.FindNext, "F9");
        keyboard.ApplyTo(editor);
        var window = new Window { Width = 900, Height = 400, Content = editor };
        window.Show();
        try
        {
            editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.None);
            Assert.True(editor.SearchPanel.IsOpened);
            window.UpdateLayout();
            var nextButtons = editor.SearchPanel.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Command == SearchCommands.FindNext).ToArray();
            Assert.Equal(2, nextButtons.Length);
            Assert.All(nextButtons, next => Assert.Equal("Find Next (F9)", ToolTip.GetTip(next)));
            editor.SearchPanel.Close();
            editor.TextArea.Focus();
            var command = keyboard.IsMac ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPressQwerty(PhysicalKey.F, command);
            Assert.True(editor.SearchPanel.IsClosed);
            window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
            Assert.True(editor.SearchPanel.IsReplaceMode);
            keyboard.SetKey(KeyCommands.FindNext, "");
            keyboard.ApplyTo(editor);
            Assert.All(nextButtons, next => Assert.Equal("Find Next", ToolTip.GetTip(next)));
            editor.SearchPanel.Close();
            keyboard.SetKey(KeyCommands.FindText, "");
            keyboard.ApplyTo(editor);
            window.KeyPressQwerty(PhysicalKey.F7, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.F, command);
            Assert.True(editor.SearchPanel.IsClosed);
            window.Content = other;
            window.UpdateLayout();
            other.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.F, command);
            Assert.True(other.SearchPanel.IsOpened);
        }
        finally { window.Close(); }
    }
}
