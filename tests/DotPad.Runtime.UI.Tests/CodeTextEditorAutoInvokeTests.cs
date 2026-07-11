using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #51: completion used to only show up after '.' or an explicit Ctrl+Space, never while just
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
        Assert.True(CodeTextEditor.IsStartOfNewIdentifier(text, caretOffset, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Theory]
    [InlineData("so", 2)] // second letter of the same identifier
    [InlineData("x3", 2)] // digit is a universally valid identifier-continuation character
    public void IsStartOfNewIdentifier_Should_BeFalse_WhenContinuingAnIdentifierAlreadyBeingTyped(
        string text, int caretOffset)
    {
        Assert.False(CodeTextEditor.IsStartOfNewIdentifier(text, caretOffset, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsStartOfNewIdentifier_Should_BeFalse_RightAfterARegisteredTriggerCharacter()
    {
        // '.' already has its own trigger (CompletionTriggers) that opens the window with
        // context-specific results — auto-invoking again on the next letter would replace those with
        // the unrelated general symbol list one keystroke later.
        Assert.False(CodeTextEditor.IsStartOfNewIdentifier("x.s", 3, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsStartOfNewIdentifier_Should_TreatUnderscoreAsIdentifierContinuation_OnlyWhenConfigured()
    {
        // '_' isn't universal — a language that doesn't configure it as an identifier char should see
        // it as a plain word boundary instead, the same as any other punctuation.
        Assert.False(CodeTextEditor.IsStartOfNewIdentifier("_s", 2, CSharpIdentifierChars, CSharpTriggerChars));
        Assert.True(CodeTextEditor.IsStartOfNewIdentifier("_s", 2, NoExtraChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsMemberAccessContext_Should_BeTrue_RightAfterATriggerCharacter_SkippingThePartialIdentifier()
    {
        Assert.True(CodeTextEditor.IsMemberAccessContext("x.Wri", 5, CSharpIdentifierChars, CSharpTriggerChars));
    }

    [Fact]
    public void IsMemberAccessContext_Should_BeFalse_AtStatementLevel()
    {
        Assert.False(CodeTextEditor.IsMemberAccessContext("var x", 5, CSharpIdentifierChars, CSharpTriggerChars));
    }
}
