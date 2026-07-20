using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task TypingACommitCharacter_Should_AcceptTheSelection_ByDefault()
    {
        var (window, editor) = CreateEditor("", ("Rhino", 1.0));

        window.KeyTextInput("R");
        await Task.Delay(20);
        window.KeyTextInput(";");
        await Task.Delay(20);

        // ';' both commits the selection ("R" -> "Rhino") and is itself inserted as typed.
        Assert.Equal("Rhino;", editor.Document.Text);
    }

    // TODO.md #60.4: a user who finds punctuation unpredictably accepting completions can turn
    // this off — Enter/Tab/click still commit either way (untouched by this switch), only the
    // "typing punctuation also commits" convenience path is gated.
    [AvaloniaFact]
    public async Task TypingACommitCharacter_Should_NotAcceptTheSelection_WhenCommitCharactersAreDisabled()
    {
        var (window, editor) = CreateEditor("", ("Rhino", 1.0));
        editor.CommitCharactersEnabled = false;

        window.KeyTextInput("R");
        await Task.Delay(20);
        window.KeyTextInput(";");
        await Task.Delay(20);

        // No commit happened — ';' was just inserted after the still-partial "R".
        Assert.Equal("R;", editor.Document.Text);
    }

    // TODO.md #60.3: "MyContext" only substring-matches "Con" (no prefix — GetMatchQuality's
    // "match start" tier, quality >= 5, never fires), so it's only soft-selected: shown
    // highlighted, but not confident enough for a typed commit character to accept automatically.
    [AvaloniaFact]
    public async Task TypingACommitCharacter_Should_NotAcceptASoftSelectedMatch()
    {
        var (window, editor) = CreateEditor("", ("MyContext", 1.0));

        window.KeyTextInput("C");
        await Task.Delay(20);
        window.KeyTextInput("o");
        await Task.Delay(20);
        window.KeyTextInput("n");
        await Task.Delay(20);
        window.KeyTextInput(";");
        await Task.Delay(20);

        // No commit happened — ';' was just inserted after the still-partial "Con".
        Assert.Equal("Con;", editor.Document.Text);
    }

    // Same soft-selected match as above, but committed via Enter — an explicit accept gesture
    // always wins regardless of confidence; only the "typing punctuation also commits" convenience
    // path (CompletionWindow.CompletionList_InsertionRequested's TextInputEventArgs check) is
    // gated by soft-selection.
    [AvaloniaFact]
    public async Task PressingEnter_Should_AcceptASoftSelectedMatch()
    {
        var (window, editor) = CreateEditor("", ("MyContext", 1.0));

        window.KeyTextInput("C");
        await Task.Delay(20);
        window.KeyTextInput("o");
        await Task.Delay(20);
        window.KeyTextInput("n");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        Assert.Equal("MyContext", editor.Document.Text);
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

    // TODO.md #60.5: committing via Tab with the caret mid-word ("Cons|ole") must consume the
    // pre-existing remainder ("ole"), producing exactly the accepted item's text — not leave it
    // dangling after the inserted text (e.g. "Consoleole").
    [AvaloniaFact]
    public async Task CommittingWithTab_Should_ReplaceTheRemainderOfTheWordAfterTheCaret()
    {
        // Arrange — the whole word "Console" is already in the document; the caret is moved back to
        // its middle, as if the user had clicked back into an already-typed identifier.
        var (window, editor) = CreateEditor("Console", ("Console", 1.0));
        editor.TextArea.Caret.Offset = 4; // "Cons|ole"

        // Act — Ctrl+Space (explicit invoke) pre-selects "Console" from the "Cons" already typed
        // before the caret (same SelectItem(alreadyTyped) mechanism as the auto-invoke pre-selection
        // above), then Tab commits it.
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        await Task.Delay(20);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await Task.Delay(20);

        // Assert
        Assert.Equal("Console", editor.Document.Text);
    }

    // Same setup as above, but committing via Enter — the spec (TODO.md #60.5) deliberately keeps
    // Enter's existing "insert, don't touch what follows the caret" behavior, only Tab replaces the
    // remainder.
    [AvaloniaFact]
    public async Task CommittingWithEnter_Should_NotTouchTheRemainderAfterTheCaret()
    {
        var (window, editor) = CreateEditor("Console", ("Console", 1.0));
        editor.TextArea.Caret.Offset = 4; // "Cons|ole"

        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        Assert.Equal("Consoleole", editor.Document.Text);
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
        // expanding the "for" snippet. AvaloniaEdit's TextUtilities.GetNewLineFromDocument falls back
        // to Environment.NewLine when the document has no existing line delimiter to copy (true here
        // — the document started empty) — "\r\n" on Windows, "\n" on Linux/macOS, so the expected
        // value can't be a hardcoded "\n" (this exact CI-only mismatch is why the assertion needs to
        // follow the same platform default rather than assume Unix).
        Assert.Equal($"f{Environment.NewLine}", editor.Document.Text);
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

    // TODO.md #65: the provider (EditorTabItemControl) detects a fresh, not-yet-typed call argument
    // and sets RedirectToOverloads on an explicit-invoke (Ctrl+Space) request — ShowCompletion must
    // show overloads instead of whatever completion items the provider may (or may not) have added,
    // not open a completion window at all.
    [AvaloniaFact]
    public async Task ShowCompletion_Should_ShowOverloads_WhenProviderRedirects()
    {
        // Arrange
        var overloadProviderCallCount = 0;
        var editor = new CodeTextEditor();
        editor.Document = new TextDocument("Foo(");
        editor.AsyncCompletionProvider = e =>
        {
            e.RedirectToOverloads = true;
            e.Items.Add(new CompletionData("ShouldNeverAppear")); // proves Items is ignored
            return Task.CompletedTask;
        };
        editor.AsyncOverloadProvider = e =>
        {
            overloadProviderCallCount++;
            e.Overloads.Add(("Foo(int x)", "some overload"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act — the literal Ctrl+Space hotkey (trigger '\0', IsExplicitInvoke true).
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        await Task.Delay(20);

        // Assert
        Assert.Equal(1, overloadProviderCallCount);
        Assert.True(editor.IsInsightWindowOpen);
        Assert.False(editor.IsCompletionWindowOpen);
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

    // TODO.md item 60 point 8 — EditorTabItemControl builds a SnippetCompletionData (not a plain
    // EditorCompletionData) for an override-member stub, reusing CodeTextEditor.ExpandSnippet's
    // reindent-to-current-line + ${0} cursor placement. This proves accepting one through the real
    // completion window drives that same path end to end, not just that the service produces the
    // right template string (covered separately, headlessly, by CSharpIntelliSenseOverrideStubTests).
    [AvaloniaFact]
    public async Task AcceptingASnippetCompletionData_Should_ExpandTheTemplate_ReindentedToTheCurrentLine()
    {
        // Arrange — caret sits right after 4 spaces of indent on the current (only) line.
        const string template = "public override void Foo()\n{\n\tbase.Foo();${0}\n}";
        var editor = new CodeTextEditor();
        editor.Document = new TextDocument("    ");
        editor.AsyncCompletionProvider = e =>
        {
            e.Items.Add(new SnippetCompletionData(
                "Foo", template, image: null,
                segment => editor.ExpandSnippet(segment.Offset, segment.Length, template)));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act — Ctrl+Space (explicit invoke), type a prefix matching the item so it's actually
        // selected (mirrors AcceptingACompletion_Should_InvokeOnCommit_WithTheItemsTags above — Enter
        // with nothing typed/selected doesn't commit anything), then Enter to accept it.
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        await Task.Delay(20);
        window.KeyTextInput("F");
        await Task.Delay(20);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Task.Delay(20);

        // Assert — every line after the first gets the same 4-space indent as the current line, and
        // the ${0} marker is gone with the caret left exactly where it was (right after "base.Foo();").
        var tab = editor.Options.IndentationString;
        var expected = "    public override void Foo()\n" +
            "    {\n" +
            $"    {tab}base.Foo();\n" +
            "    }";
        Assert.Equal(expected, editor.Document.Text);
        Assert.Equal(("    public override void Foo()\n    {\n    " + tab + "base.Foo();").Length,
            editor.TextArea.Caret.Offset);
    }

    // TODO.md #53 remainder: the active parameter used to update only on a typed "," — clicking,
    // arrow keys, Home/End left it stuck on whatever slot was active when the window opened.
    [AvaloniaFact]
    public async Task ShowOverloads_Should_ReRequest_WhenTheCaretMovesWhileTheWindowIsOpen()
    {
        // Arrange
        var callCount = 0;
        var editor = new CodeTextEditor();
        editor.OverloadTriggers.Add('(');
        editor.Document = new TextDocument("Foo");
        editor.AsyncOverloadProvider = e =>
        {
            callCount++;
            e.Overloads.Add(("Foo(int a, int b)", "desc"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = 3;

        // Act — open the insight window via the trigger character, then move the caret without
        // typing anything (simulating a click/arrow-key navigation, not a "," trigger).
        window.KeyTextInput("(");
        await Task.Delay(20);
        Assert.True(editor.IsInsightWindowOpen);
        var callsAfterOpen = callCount;

        editor.TextArea.Caret.Offset = 1;
        await Task.Delay(20);

        // Assert — a second request went out purely from the caret move.
        Assert.True(callCount > callsAfterOpen);
    }

    // Companion to the test above — once a re-request comes back with no overloads at all, the
    // now-stale window must close instead of continuing to show the old call's parameters (the gap
    // ShowOverloads' pre-existing "Overloads.Count == 0 -> just return" left open). Triggered here via
    // a second typed "," rather than a caret move: an AvaloniaFact investigation for this same fix
    // found that AvaloniaEdit's own OverloadInsightWindow already closes itself on any caret move
    // regardless of this fix (confirmed empirically — the window closed even with the fix reverted),
    // which would make a caret-move-driven version of this test pass either way and prove nothing.
    // The typed-"," path doesn't go through caret-move re-triggering at all (it's the pre-existing
    // OverloadTriggers path in OnTextEntered), so it isolates ShowOverloads' own empty-result handling
    // from that separate, already-present AvaloniaEdit behavior.
    [AvaloniaFact]
    public async Task ShowOverloads_Should_CloseTheWindow_WhenARequestComesBackWithNoOverloads()
    {
        // Arrange
        var returnOverload = true;
        var editor = new CodeTextEditor();
        editor.OverloadTriggers.Add('(');
        editor.OverloadTriggers.Add(',');
        editor.Document = new TextDocument("Foo1");
        editor.AsyncOverloadProvider = e =>
        {
            if (returnOverload) e.Overloads.Add(("Foo(int a, int b)", "desc"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = 3;

        window.KeyTextInput("(");
        await Task.Delay(20);
        Assert.True(editor.IsInsightWindowOpen);

        // Act — a second trigger character, but this time the provider finds nothing (e.g. the
        // service determined the caret is no longer inside any call).
        returnOverload = false;
        window.KeyTextInput(",");
        await Task.Delay(20);

        // Assert
        Assert.False(editor.IsInsightWindowOpen);
    }

    // TODO.md #60.9: Esc closing the insight window — already worked via
    // CompletionWindowBase.OnKeyDown's fully generic Key.Escape handler (OverloadInsightWindow calls
    // base.OnKeyDown first, before its own Up/Down cycling), but had no regression test locking that
    // behavior in.
    [AvaloniaFact]
    public async Task Escape_Should_CloseTheInsightWindow()
    {
        // Arrange
        var editor = new CodeTextEditor();
        editor.OverloadTriggers.Add('(');
        editor.Document = new TextDocument("Foo");
        editor.AsyncOverloadProvider = e =>
        {
            e.Overloads.Add(("Foo(int a, int b)", "desc"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = 3;

        window.KeyTextInput("(");
        await Task.Delay(20);
        Assert.True(editor.IsInsightWindowOpen);

        // Act
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Task.Delay(20);

        // Assert
        Assert.False(editor.IsInsightWindowOpen);
    }

    // TODO.md #53 remainder: any closing bracket used to hide the window unconditionally, including
    // one closing a bracket NESTED inside an already-typed argument — e.g. typing the "}" that closes
    // an array-literal argument (Foo(new[] { 1, 2 }) incorrectly hid the window for Foo's own,
    // still-open call.
    [AvaloniaFact]
    public async Task InsightWindow_Should_StayOpen_WhenClosingABracketNestedInsideAnArgument()
    {
        // Arrange
        var editor = new CodeTextEditor();
        editor.OverloadTriggers.Add('(');
        editor.BracePairs['('] = ')';
        editor.BracePairs['{'] = '}';
        editor.Document = new TextDocument("Foo");
        editor.AsyncOverloadProvider = e =>
        {
            e.Overloads.Add(("Foo(int[] xs)", "desc"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = 3;

        // Act — open the call, type a nested "{" (e.g. the start of an array-literal argument), then
        // its closing "}".
        window.KeyTextInput("(");
        await Task.Delay(20);
        Assert.True(editor.IsInsightWindowOpen);

        window.KeyTextInput("{");
        await Task.Delay(20);
        window.KeyTextInput("}");
        await Task.Delay(20);

        // Assert — the OUTER call's window is still showing (it never should have closed at all).
        Assert.True(editor.IsInsightWindowOpen);
    }

    // Companion regression guard — closing the call's OWN bracket (nothing nested pending) must still
    // hide the window immediately, same as before this fix.
    [AvaloniaFact]
    public async Task InsightWindow_Should_Close_WhenClosingTheCallsOwnBracket()
    {
        // Arrange
        var editor = new CodeTextEditor();
        editor.OverloadTriggers.Add('(');
        editor.BracePairs['('] = ')';
        editor.Document = new TextDocument("Foo");
        editor.AsyncOverloadProvider = e =>
        {
            e.Overloads.Add(("Foo(int x)", "desc"));
            return Task.CompletedTask;
        };
        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = 3;

        // Act
        window.KeyTextInput("(");
        await Task.Delay(20);
        Assert.True(editor.IsInsightWindowOpen);

        window.KeyTextInput(")");
        await Task.Delay(20);

        // Assert
        Assert.False(editor.IsInsightWindowOpen);
    }

    // TODO.md #77 — user-reported bug, reproduced end-to-end with the REAL Roslyn-backed
    // CSharpIntelliSenseService (not a fake AsyncCompletionProvider like every other test in this
    // file): "var a = new List<string>();" then "a" on its own line then "." replaced "a" with
    // "args". Root cause was a Roslyn quirk (see CSharpIntelliSenseEndOfBufferTests /
    // CSharpIntelliSenseService.TrailingPad) — the local variable dropped out of LookupSymbols
    // entirely at that exact caret position, not a ranking tie-break — so this drives the actual
    // reported keystrokes through a live completion window to prove the fix holds up under the real
    // 24/7-auto-invoke + commit-character pipeline, not just at the raw GetCompletionsAsync level.
    [AvaloniaFact]
    public async Task TypingDot_Should_CommitTheLocalVariable_NotTheImplicitArgsParameter()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        using var intelliSense = new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);

        var editor = new CodeTextEditor();
        editor.CompletionTriggers.Add('.');
        editor.IdentifierChars.Add('_');
        editor.Document = new TextDocument("var a = new System.Collections.Generic.List<string>();\n");
        editor.AsyncCompletionProvider = async e =>
        {
            var trigger = e.IsExplicitInvoke ? CompletionTrigger.Invoked : CompletionTrigger.Insertion(e.Trigger);
            var list = await intelliSense.GetCompletionsAsync(editor.Text, e.CaretOffset, trigger, e.CancellationToken);
            if (list is null) { e.SuppressSnippets = true; return; }
            foreach (var item in list.Items)
                e.Items.Add(new EditorCompletionData(item.DisplayText, () => item.DisplayText, item.Priority));
        };

        var window = new Window { Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.TextArea.Caret.Offset = editor.Document.TextLength;

        // Act — types "a" (auto-invokes on the fresh identifier's first letter, caret now sits at the
        // literal end of the document, nothing typed after it), then "." — the exact reported repro.
        // Unlike every other test in this file, the provider here runs a REAL Roslyn semantic query
        // (MEF composition, workspace/compilation, binding) instead of resolving instantly, so a fixed
        // short delay isn't reliable — polling for the window to actually open is what makes this
        // deterministic regardless of how long that first real query takes.
        window.KeyTextInput("a");
        for (var i = 0; i < 250 && !editor.IsCompletionWindowOpen; i++)
            await Task.Delay(20);
        Assert.True(editor.IsCompletionWindowOpen);

        window.KeyTextInput(".");
        await Task.Delay(100);

        // Assert
        Assert.Equal("var a = new System.Collections.Generic.List<string>();\na.", editor.Document.Text);
    }
}
