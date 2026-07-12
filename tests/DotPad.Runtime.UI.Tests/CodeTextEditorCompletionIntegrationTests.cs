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
                e.Items.Add(new EditorCompletionData(text, () => text, priority));
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

    private static (Window Window, CodeTextEditor Editor) CreateEditorWithProvider(
        string initialText, Func<CompletionRequestedEventArgs, Task> provider)
    {
        var editor = new CodeTextEditor();
        editor.IdentifierChars.Add('_');
        editor.CompletionTriggers.Add('.');
        editor.Document = new TextDocument(initialText);
        editor.AsyncCompletionProvider = provider;

        var window = new Window { Content = editor };
        window.Show();
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

    // ANALYSIS.md #57: fast typing can start a second completion request before the first one
    // resolves (e.g. the 24/7 auto-invoke on a letter, immediately followed by '.'). If the first
    // (now stale) request happens to resolve *after* the second, newer one — plausible, since async
    // completion order isn't guaranteed — it used to still open its own window and overwrite
    // _completionWindow, silently orphaning the newer window (whose own Closed handler would then
    // null out the wrong reference). The provider here deliberately ignores the cancellation token,
    // simulating a provider that doesn't observe it, to prove the belt-and-suspenders check in
    // ShowCompletion (not just real cancellation) is what actually stops the stale response.
    [AvaloniaFact]
    public async Task ShowCompletion_Should_IgnoreAStaleResponse_ThatResolvesAfterASupersedingRequest()
    {
        // Arrange
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        var callCount = 0;

        var (window, editor) = CreateEditorWithProvider("", async e =>
        {
            callCount++;
            if (callCount == 1)
            {
                await tcs1.Task; // the stale, superseded request
                e.Items.Add(new CompletionData("Zzz1"));
            }
            else
            {
                await tcs2.Task; // the newer, superseding request
                e.Items.Add(new CompletionData("Rhino2"));
            }
        });

        // Act — 'C' auto-invokes (request #1, blocked on tcs1); '.' is an unconditional
        // CompletionTrigger, firing request #2 (blocked on tcs2) regardless of #1 still being open.
        window.KeyTextInput("C");
        await Task.Delay(20);
        window.KeyTextInput(".");
        await Task.Delay(20);

        // The newer request resolves first, then the stale one resolves late.
        tcs2.SetResult();
        await Task.Delay(20);
        tcs1.SetResult();
        await Task.Delay(20);

        // Nothing is pre-selected right after '.' (no member-name prefix typed yet — see
        // ANALYSIS.md #55's alreadyTyped check), so Enter has nothing to accept until the list is
        // filtered down to one candidate by typing part of its name, same as a real user would.
        window.KeyTextInput("R");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert — only the newer request's window should ever exist; accepting it must insert
        // "Rhino2" after "C.", not have the stale "Zzz1" response clobber it.
        Assert.Equal("C.Rhino2", editor.Document.Text);
    }

    // User-reported bug: snippets still showed up while typing inside a comment/string. The
    // provider (EditorTabItemControl) sets SuppressSnippets when the language service says this
    // position isn't a valid completion spot at all — ShowCompletion must respect that instead of
    // unconditionally offering snippets on every explicit/auto-invoke.
    [AvaloniaFact]
    public async Task ShowCompletion_Should_NotOfferSnippets_WhenProviderSuppressesThem()
    {
        // Arrange
        var editor = new CodeTextEditor();
        editor.Snippets["for"] = "for (...) { }";
        editor.AsyncCompletionProvider = e =>
        {
            e.SuppressSnippets = true; // simulates the provider detecting a comment/string position
            return Task.CompletedTask;
        };
        editor.Document = new TextDocument("");
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act — 'f' auto-invokes; would normally offer the "for" snippet (nothing else does, and
        // this isn't a member-access context), if not for SuppressSnippets.
        window.KeyTextInput("f");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert — no completion window ever opened, so Enter just inserts a newline instead of
        // expanding the "for" snippet.
        Assert.Equal("f\n", editor.Document.Text);
    }

    // User-reported bug: an empty completion popup stayed visible (IsOpen/IsVisible both true, zero
    // items) when nothing typed so far matched any offered item. The manual CompletionList.SelectItem
    // call ShowCompletion makes right after Show() (see ANALYSIS.md #55) bypasses CompletionWindow's
    // own CaretPositionChanged handler — the one that normally hides the window once filtering
    // leaves nothing to show — since that handler only runs on the *next* caret move, not for this
    // first, manually-triggered filter.
    [AvaloniaFact]
    public async Task ShowCompletion_Should_HideTheWindow_WhenNothingTypedMatchesAnyItem()
    {
        // Arrange — only "Rhino" is ever offered.
        var (window, editor) = CreateEditor("", ("Rhino", 1.0));

        // Act — 'Q' auto-invokes and filters against "Rhino" — zero matches, so the window should
        // close instead of sitting open and empty.
        window.KeyTextInput("Q");
        await Task.Delay(20);

        // Assert
        Assert.False(editor.IsCompletionWindowOpen);
    }

    // User-reported bug: typing a lambda parameter name (e.g. "list.First(x" then "=" to start
    // "x => ...") committed the auto-invoked completion's selected match instead of just inserting
    // '='. 'x' auto-invokes (it's a fresh identifier) and offers whatever happens to match/rank
    // highest — here a same-named class — and '=' used to be a commit character like any other
    // punctuation, accepting that match right before the "=>" the user was actually typing.
    [AvaloniaFact]
    public async Task TypingEquals_Should_NotCommitTheSelectedItem_WhileStartingALambdaArrow()
    {
        // Arrange — "XmlClass" is the only offered item, so it's always selected regardless of
        // quality/priority ranking; stands in for whatever real symbol happens to match "x".
        var (window, editor) = CreateEditor("list.First(", ("XmlClass", 1.0));

        // Act
        window.KeyTextInput("x");
        await Task.Delay(20);
        window.KeyTextInput("=");
        await Task.Delay(20);

        // Assert — '=' must just be inserted, not commit "XmlClass" over the lambda parameter "x".
        Assert.Equal("list.First(x=", editor.Document.Text);
    }

    // TODO.md #28 remainder: accepting a completion item for a type from an added-but-not-yet-
    // imported NuGet package (e.g. Newtonsoft.Json's JsonConvert) should auto-add its `using`, same
    // as the existing lightbulb quick-fix. CodeTextEditor/EditorCompletionData don't know what a
    // "using" is (language-agnostic control layer) — they just carry opaque tags through to
    // whatever onCommit callback the language-aware caller (EditorTabItemControl) supplied.
    [AvaloniaFact]
    public async Task AcceptingACompletion_Should_InvokeOnCommit_WithTheItemsTags()
    {
        // Arrange
        IReadOnlyList<string>? committedTags = null;
        var editor = new CodeTextEditor();
        editor.Document = new TextDocument("");
        editor.AsyncCompletionProvider = e =>
        {
            e.Items.Add(new EditorCompletionData(
                "JsonConvert", () => "JsonConvert", priority: 1.0,
                tags: ["Newtonsoft.Json"], onCommit: tags => committedTags = tags));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act
        window.KeyTextInput("J");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert
        Assert.Equal("JsonConvert", editor.Document.Text);
        Assert.NotNull(committedTags);
        Assert.Contains("Newtonsoft.Json", committedTags!);
    }

    // Ordinary completion items (no tags — the overwhelming majority) must not pay for, or trigger,
    // any onCommit machinery at all.
    [AvaloniaFact]
    public async Task AcceptingACompletion_Should_NotInvokeOnCommit_WhenTheItemHasNoTags()
    {
        // Arrange
        var onCommitCallCount = 0;
        var editor = new CodeTextEditor();
        editor.Document = new TextDocument("");
        editor.AsyncCompletionProvider = e =>
        {
            e.Items.Add(new EditorCompletionData(
                "Rhino", () => "Rhino", priority: 1.0, onCommit: _ => onCommitCallCount++));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act
        window.KeyTextInput("R");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert
        Assert.Equal("Rhino", editor.Document.Text);
        Assert.Equal(0, onCommitCallCount);
    }
}
