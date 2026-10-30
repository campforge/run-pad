using Xunit;

namespace Invoklet.NonUI.Tests.Avalonia.CodeTextEditor;

// Completion used to only show up after '.' or an explicit Ctrl+Space, never while just
// typing an identifier (LINQPad/VS Code's "24/7 IntelliSense"). CodeTextEditor.IsStartOfNewIdentifier
// decides whether a just-typed letter should auto-invoke completion — it must fire exactly once per
// identifier (the first letter), not once per keystroke, since re-invoking the language service on
// every character was the performance risk flagged when this was implemented; AvaloniaEdit's own
// CompletionWindow re-filters what's already fetched as more letters follow (see CompletionList.
// GetMatchQuality, which already does camelCase/substring matching once the window is open).
//
// Both methods take the trigger/identifier-char sets as parameters rather than hardcoding any
// particular language's lexical rules (e.g. '.' for member access, '_' as an identifier character) —
// CodeTextEditor is language-agnostic; EditorTabItemControl is what configures those sets for C#/VB.
public class CodeTextEditorAutoInvokeTests
{
    private static readonly HashSet<char> CSharpIdentifierChars = ['_'];
    private static readonly HashSet<char> CSharpTriggerChars = ['.'];
    private static readonly HashSet<char> NoExtraChars = [];

    [Theory]
    [InlineData("", 0)] // very first character typed into an empty document
    [InlineData(" s", 2)] // after whitespace
    [InlineData("(s", 2)] // after an open paren
    [InlineData("+s", 2)] // after an operator
    [InlineData(";\ns", 3)] // after a statement terminator + newline
    public void IsStartOfNewIdentifier_Should_BeTrue_RightAfterANonIdentifierCharacter(string text, int caretOffset)
    {
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier(text, caretOffset, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Theory]
    [InlineData("so", 2)] // second letter of the same identifier
    [InlineData("x3", 2)] // digit is a universally valid identifier-continuation character
    public void IsStartOfNewIdentifier_Should_BeFalse_WhenContinuingAnIdentifierAlreadyBeingTyped(
        string text, int caretOffset)
    {
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier(text, caretOffset, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsStartOfNewIdentifier_Should_BeFalse_RightAfterARegisteredTriggerCharacter()
    {
        // '.' already has its own trigger (CompletionTriggers) that opens the window with
        // context-specific results — auto-invoking again on the next letter would replace those with
        // the unrelated general symbol list one keystroke later.
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier("x.s", 3, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsStartOfNewIdentifier_Should_TreatUnderscoreAsIdentifierContinuation_OnlyWhenConfigured()
    {
        // '_' isn't universal — a language that doesn't configure it as an identifier char should see
        // it as a plain word boundary instead, the same as any other punctuation.
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier("_s", 2, CSharpIdentifierChars, CSharpTriggerChars));
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier("_s", 2, NoExtraChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsMemberAccessContext_Should_BeTrue_RightAfterATriggerCharacter_SkippingThePartialIdentifier()
    {
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsMemberAccessContext("x.Wri", 5, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsMemberAccessContext_Should_BeFalse_AtStatementLevel()
    {
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsMemberAccessContext("var x", 5, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Theory]
    [InlineData("List<", 5)] // right after a type name — generic type argument
    [InlineData("x <", 3)] // preceded by a space, not an identifier — plain comparison operator
    [InlineData("<", 1)] // nothing before it at all
    public void IsContextualTriggerContext_Should_OnlyBeTrue_RightAfterAnIdentifierCharacter(
        string text, int caretOffset)
    {
        var expected = text == "List<";
        Assert.Equal(expected, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsContextualTriggerContext(text, caretOffset, CSharpIdentifierChars));
    }

    [Fact]
    public void IsWordTriggerContext_Should_BeTrue_RightAfterARegisteredWord()
    {
        var wordTriggers = new HashSet<string> { "new" };
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsWordTriggerContext("var x = new ", 12, CSharpIdentifierChars, wordTriggers));
    }

    [Theory]
    [InlineData("renew ", 6)] // a longer word that happens to end with "new" is a different word
    [InlineData("foo ", 4)] // an unrelated word
    public void IsWordTriggerContext_Should_BeFalse_ForADifferentOrUnrelatedWord(string text, int caretOffset)
    {
        var wordTriggers = new HashSet<string> { "new" };
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsWordTriggerContext(text, caretOffset, CSharpIdentifierChars, wordTriggers));
    }

    [Fact]
    public void IsWordTriggerContext_Should_BeFalse_WhenNoWordsAreRegistered()
    {
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsWordTriggerContext("new ", 4, CSharpIdentifierChars, new HashSet<string>()));
    }

    // Regression guard: ShowCompletion captures the caret offset before `await`-ing the language
    // service, then reads the document's *current* text afterwards. Typing (or deleting) fast enough
    // while that request is in flight leaves the captured offset pointing past the now-shorter text —
    // these methods must clamp instead of indexing out of range (this crashed the app in practice
    // once auto-invoke made ShowCompletion fire on almost every keystroke instead of only on Ctrl+Space).
    [Fact]
    public void AllContextMethods_Should_NotThrow_WhenOffsetIsStaleAndPastTheCurrentTextLength()
    {
        const string text = "x";
        const int staleOffset = 50;

        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsMemberAccessContext(text, staleOffset, CSharpIdentifierChars, CSharpTriggerChars));
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsStartOfNewIdentifier(text, staleOffset, CSharpIdentifierChars, CSharpTriggerChars));
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsContextualTriggerContext(text, staleOffset, CSharpIdentifierChars));
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsWordTriggerContext(text, staleOffset, CSharpIdentifierChars, new HashSet<string> { "new" }));
        Assert.Equal(0, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.GetWordStart(text, staleOffset, CSharpIdentifierChars));
    }

    // Regression guard: the 24/7 auto-invoke opens the completion window one character *after* the
    // word already started (the trigger fires once the first letter is already in the document), so
    // without this, accepting a completion item inserted it after the already-typed prefix instead of
    // replacing the whole word (e.g. "Wri" + selecting "WriteLine" -> "WriWriteLine").
    [Theory]
    [InlineData("Wri", 3, 0)] // whole word typed since the window "should" have opened at offset 0
    [InlineData("x.Wri", 5, 2)] // only back to the '.', not through it — that's the member-access prefix
    [InlineData("var x", 5, 4)] // back to the last identifier ("x"), not the keyword before it
    public void GetWordStart_Should_WalkBackToTheStartOfTheWordBeingCompleted(
        string text, int offset, int expectedStart)
    {
        Assert.Equal(expectedStart, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.GetWordStart(text, offset, CSharpIdentifierChars));
    }

    [Fact]
    public void GetWordStart_Should_NotWalkPastANonIdentifierTriggerCharacter()
    {
        // Right after '.' (nothing of the member name typed yet) there's nothing to walk back over.
        Assert.Equal(2, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.GetWordStart("x.", 2, CSharpIdentifierChars));
    }

    // A Tab-triggered commit consumes any pre-existing remainder of the word the
    // caret is inside — GetWordEnd finds where that remainder stops, mirroring GetWordStart above.
    [Theory]
    [InlineData("Console", 4, 7)] // "Cons|ole" — caret mid-word, "ole" is the remainder to consume
    [InlineData("Console", 7, 7)] // caret already at the end — nothing left to walk over
    [InlineData("my_field", 2, 8)] // '_' only counts as part of the remainder when configured
    public void GetWordEnd_Should_WalkForwardToTheEndOfTheWordBeingCompleted(
        string text, int offset, int expectedEnd)
    {
        Assert.Equal(expectedEnd, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.GetWordEnd(text, offset, CSharpIdentifierChars));
    }

    [Fact]
    public void GetWordEnd_Should_NotWalkPastANonIdentifierCharacter()
    {
        // "Cons|.Wri" — a '.' right after the caret isn't part of the remainder.
        Assert.Equal(4, Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.GetWordEnd("Cons.Wri", 4, CSharpIdentifierChars));
    }

    // Typing '_' mid-identifier used to commit the completion window's selected item
    // (only char.IsLetterOrDigit was excluded, not the language's own IdentifierChars), and space
    // committed unconditionally — swapping a still-being-typed word for an unrelated prefix match
    // (e.g. "var ret " -> "var return "). '=' has the same problem for lambda parameters (user-
    // reported: "list.First(x" then "=" to start "x => ..." committed whatever matched "x"). All
    // three must never commit.
    [Theory]
    [InlineData('_')] // identifier-continuation char, configured via IdentifierChars
    [InlineData(' ')] // never commits, regardless of IdentifierChars/CompletionTriggers
    [InlineData('=')] // starting "=>" for a lambda parameter must not commit the parameter name
    [InlineData('a')]
    [InlineData('3')]
    public void IsCommitCharacter_Should_BeFalse_ForIdentifierContinuationOrSpace(char ch)
    {
        Assert.False(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsCommitCharacter(ch, CSharpIdentifierChars));
    }

    [Theory]
    [InlineData('.')]
    [InlineData('(')]
    [InlineData(';')]
    [InlineData('<')]
    public void IsCommitCharacter_Should_BeTrue_ForPunctuationOutsideIdentifierChars(char ch)
    {
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsCommitCharacter(ch, CSharpIdentifierChars));
    }

    [Fact]
    public void IsCommitCharacter_Should_BeTrue_ForUnderscore_WhenNotConfiguredAsAnIdentifierChar()
    {
        // '_' isn't universal (see IsStartOfNewIdentifier's own underscore test) — a language that
        // doesn't register it as an identifier char should see it as ordinary punctuation instead.
        Assert.True(Invoklet.Avalonia.CodeTextEditor.CodeTextEditor.IsCommitCharacter('_', NoExtraChars));
    }
}
