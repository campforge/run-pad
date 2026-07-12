using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md #54/#55 — end-to-end regressions for real completion-window mechanics that the pure
// static-function tests in CodeTextEditorAutoInvokeTests.cs can't reach (they need a live
// CompletionWindow/TextArea, which needs a real headless Avalonia window).
public class CodeTextEditorCompletionIntegrationTests
{
    private static (Window Window, CodeTextEditor Editor) CreateEditor(
        string initialText, params (string Text, double Priority)[] items)
    {
        var editor = new CodeTextEditor();
        editor.IdentifierChars.Add('_');
        editor.CompletionTriggers.Add('.');
        editor.Document = new TextDocument(initialText);
        editor.AsyncCompletionProvider = e =>
        {
            foreach (var (text, priority) in items)
                e.Items.Add(new EditorCompletionData(text, text, priority));
            return Task.CompletedTask;
        };

        var window = new Window { Content = editor };
        window.Show();
        // CodeTextEditor (a TemplatedControl wrapping AvaloniaEdit's TextEditor) isn't itself
        // Focusable — its TextArea child is (see TestAppBuilder for why the AvaloniaEdit Fluent
        // style must be registered for that child to even exist in the visual tree).
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;
        return (window, editor);
    }

    [AvaloniaFact]
    public async Task TypingSpace_Should_NotCommitASoftPrefixMatch_WhileStillTypingADifferentWord()
    {
        // Arrange — "return"/"readonly" both start with "ret", so the 24/7 auto-invoke on 'r' offers
        // them as prefix matches while the user is actually typing a new local variable name "ret".
        var (window, editor) = CreateEditor("var ", ("return", 0.1), ("readonly", 0.1));

        // Act
        window.KeyTextInput("r");
        await Task.Delay(20);
        window.KeyTextInput("e");
        await Task.Delay(20);
        window.KeyTextInput("t");
        await Task.Delay(20);
        window.KeyTextInput(" ");
        await Task.Delay(20);

        // Assert — space must not commit "return"/"readonly" over the word actually being typed.
        Assert.Equal("var ret ", editor.Document.Text);
    }

    [AvaloniaFact]
    public async Task TypingUnderscore_Should_NotCommitTheSelectedItem_WhileContinuingAnIdentifier()
    {
        // Arrange
        var (window, editor) = CreateEditor("", ("my_field", 1.0));

        // Act
        window.KeyTextInput("m");
        await Task.Delay(20);
        window.KeyTextInput("y");
        await Task.Delay(20);
        window.KeyTextInput("_");
        await Task.Delay(20);

        // Assert — '_' is a configured IdentifierChars member, so it must continue the identifier
        // being typed, not commit "my_field" out from under it.
        Assert.Equal("my_", editor.Document.Text);
    }

    [AvaloniaFact]
    public async Task AutoInvoke_Should_PreSelectTheBestMatch_FromTheFirstLetterTyped()
    {
        // Arrange — "Zebra" is added first (so it'd win if the window opened with no filtering/
        // selection and just defaulted to the first item), "Rhino" is the only real prefix match for
        // "R". If the first-letter pre-selection from ANALYSIS.md #55 didn't run, accepting the
        // selection here would either do nothing (no default selection) or commit the wrong item.
        var (window, editor) = CreateEditor("", ("Zebra", 1.0), ("Rhino", 1.0));

        // Act
        window.KeyTextInput("R");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert
        Assert.Equal("Rhino", editor.Document.Text);
    }
}
