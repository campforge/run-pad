using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using Invoklet.Avalonia.CodeTextEditor;
using TextMateSharp.Grammars;
using Xunit;

namespace Invoklet.IntegrationTests;

// A frozen installation has stopped its tokenizer (which otherwise holds a thread-pool thread for as long
// as the editor lives) but still shows what it computed, and takes up again where it left off.
public class TextMateFreezeTests
{
    private static async Task<(CodeTextEditor Editor, Color Keyword)> HighlightedEditorAsync()
    {
        var editor = new CodeTextEditor { Document = new TextDocument("class A { void M() { } }\n") };
        editor.SetLanguage(ThemeName.DarkPlus, ".cs");
        new Window { Content = editor }.Show();
        Dispatcher.UIThread.RunJobs();
        var keyword = await IlHighlightingTests.SettledColorAsync(editor, 0, c => c is { } x && x != Colors.Black);
        Assert.NotNull(keyword);
        return (editor, keyword.Value);
    }

    [AvaloniaFact]
    public async Task AFrozenEditor_Should_KeepItsColours_AndColourEditedTextOnceResumed()
    {
        var (editor, keyword) = await HighlightedEditorAsync();
        var installation = editor.TextMateInstallation!;

        installation.Freeze();

        Assert.True(installation.IsFrozen);
        Assert.Equal(keyword, IlHighlightingTests.RenderedColor(editor, 0));

        // The first line moves down with its tokens; the inserted one is not tokenized while frozen.
        editor.Document.Insert(0, "// note\n");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(keyword, IlHighlightingTests.RenderedColor(editor, 8));

        installation.Resume();

        Assert.False(installation.IsFrozen);
        Assert.Equal(keyword, await IlHighlightingTests.SettledColorAsync(editor, 8, c => c == keyword));
        editor.Document.Insert(0, "class B { }\n");
        Assert.Equal(keyword, await IlHighlightingTests.SettledColorAsync(editor, 0, c => c == keyword));
    }

    [AvaloniaFact]
    public async Task AFrozenEditor_Should_StayFrozen_WhenItsDocumentIsReplaced_AndBeDisposable()
    {
        var (editor, _) = await HighlightedEditorAsync();
        var installation = editor.TextMateInstallation!;
        installation.Freeze();

        editor.Document = new TextDocument("struct S { }\n");
        Dispatcher.UIThread.RunJobs();

        Assert.True(installation.IsFrozen);
        installation.Resume();
        installation.Resume(); // a second one is a no-op
        installation.Freeze();
        installation.Freeze();
        editor.Dispose(); // while frozen
    }

    [AvaloniaFact]
    public async Task Freezing_BeforeTheModelExists_Should_BeRemembered_AndResumingAfterItIsCreated_Should_ColourTheText()
    {
        var editor = new TextEditor();
        new Window { Content = editor }.Show();
        var installation = editor.InstallTextMate(new RegistryOptions(ThemeName.DarkPlus), initCurrentDocument: false);
        installation.SetGrammar("source.cs");

        installation.Freeze(); // no document yet, so no model to stop

        Assert.True(installation.IsFrozen);
        editor.Document = new TextDocument("class A { void M() { } }\n"); // the model is created now
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(1500);
        var frozen = IlHighlightingTests.RenderedColor(editor, 0);

        installation.Resume();

        var resumed = await IlHighlightingTests.SettledColorAsync(editor, 0, c => c != frozen);
        Assert.NotEqual(frozen, resumed); // coloured only once resumed: no tokenizer ran while frozen
        installation.Dispose();
    }
}
