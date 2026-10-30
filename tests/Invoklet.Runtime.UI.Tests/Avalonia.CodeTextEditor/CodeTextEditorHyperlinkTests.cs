using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Bug report 2026-07-27: AvaloniaEdit's built-in LinkElementGenerator/MailLinkElementGenerator run a
// plain regex over each line's raw text, with no idea a match sits inside a string literal — a URL like
// "https://test.com/" in C# source got wrapped as a clickable link, breaking that literal's normal
// syntax-highlight styling. CodeTextEditor now turns both off in its constructor (see the comment
// there); these just confirm they land off rather than at AvaloniaEdit's own true-by-default.
public class CodeTextEditorHyperlinkTests
{
    [AvaloniaFact]
    public void Constructing_Should_DisableBuiltInHyperlinkDetection()
    {
        var editor = new CodeTextEditor { Document = new TextDocument("var url = \"https://test.com/\";") };

        Assert.False(editor.Options.EnableHyperlinks);
        Assert.False(editor.Options.EnableEmailHyperlinks);
    }
}
