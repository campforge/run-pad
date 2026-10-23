using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Moq;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using TextMateSharp.Grammars;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// IL syntax highlighting in the two places that show IL: a CodeTextEditor switched between the C# and IL
// grammars (the Assembly Explorer's code pane) and the read-only IL tab of the script editor. Colours are
// read from the rendered visual lines, like EditorThemeRefreshTests does.
public class IlHighlightingTests
{
    internal const string Il = "IL_0000: ldstr \"text\" // note\n.method public static void Run() cil managed\n";
    internal static readonly int Opcode = Il.IndexOf("ldstr", StringComparison.Ordinal);
    internal static readonly int Literal = Il.IndexOf("\"text\"", StringComparison.Ordinal) + 1;
    internal static readonly int Comment = Il.IndexOf("note", StringComparison.Ordinal);
    internal static readonly int Directive = Il.IndexOf(".method", StringComparison.Ordinal);

    internal static Color? RenderedColor(TextEditor editor, int offset)
    {
        var view = editor.TextArea.TextView;
        if (TopLevel.GetTopLevel(view) is null) return null;
        view.EnsureVisualLines();
        var line = editor.Document.GetLineByOffset(offset);
        var visual = view.GetVisualLine(line.LineNumber);
        var column = offset - line.Offset;
        var element = visual?.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
        return (element?.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color;
    }

    // TextMate tokenizes on a worker: the colour at `offset` settles shortly after the text or theme change.
    internal static async Task<Color?> SettledColorAsync(TextEditor editor, int offset, Func<Color?, bool> accept)
    {
        Color? seen = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            seen = RenderedColor(editor, offset);
            if (accept(seen)) return seen;
            await Task.Delay(25);
        }
        return seen;
    }

    // The colours of the four representative IL tokens, once each has been coloured.
    internal static async Task<Color[]> IlColoursAsync(TextEditor editor, Color? previous = null)
    {
        var colours = new Color[4];
        var offsets = new[] { Opcode, Literal, Comment, Directive };
        for (var i = 0; i < offsets.Length; i++)
        {
            colours[i] = await SettledColorAsync(editor, offsets[i],
                c => c is { } color && color != Colors.Black && color != previous) ?? Colors.Black;
        }
        return colours;
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 900, Height = 600, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task CodeTextEditor_Should_SwitchBetweenCSharpAndIlGrammars_KeepingTheTheme()
    {
        var editor = new CodeTextEditor { Document = new TextDocument(Il) };
        var window = Show(editor);
        try
        {
            Assert.Null(editor.GrammarScope);
            editor.SetLanguage(ThemeName.DarkPlus, ".cs");
            Assert.Equal("source.cs", editor.GrammarScope);

            editor.SetIlGrammar();
            Assert.Equal(IlGrammar.ScopeName, editor.GrammarScope);
            var dark = await IlColoursAsync(editor);
            Assert.Equal(3, dark.Take(3).Distinct().Count()); // opcode, string and comment differ

            editor.SetTheme(ThemeName.LightPlus);
            Assert.Equal(IlGrammar.ScopeName, editor.GrammarScope); // a theme change keeps the grammar
            var light = await SettledColorAsync(editor, Opcode, c => c is { } color && color != dark[0]);
            Assert.NotEqual(dark[0], light);

            editor.SetGrammarByExtension(".cs");
            Assert.Equal("source.cs", editor.GrammarScope);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReturningToCSharp_Should_RestoreTheCSharpColours()
    {
        const string code = "var count = 1;\nclass Greeter { }\n";
        var editor = new CodeTextEditor { Document = new TextDocument(code) };
        var window = Show(editor);
        try
        {
            editor.SetLanguage(ThemeName.DarkPlus, ".cs");
            var keyword = await SettledColorAsync(editor, 0, c => c is { } color && color != Colors.Black);

            editor.SetIlGrammar(); // `var` is no IL keyword
            var inIl = await SettledColorAsync(editor, 0, c => c != keyword);
            editor.SetGrammarByExtension(".cs");
            var restored = await SettledColorAsync(editor, 0, c => c == keyword);

            Assert.NotEqual(keyword, inIl);
            Assert.Equal(keyword, restored);
            Assert.Equal(code, editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GrammarSwitches_BeforeTextMateIsInstalled_Should_BeHarmless()
    {
        var editor = new CodeTextEditor();

        editor.SetIlGrammar();
        editor.SetGrammarByExtension(".cs");

        Assert.Null(editor.GrammarScope);
    }

    // ── The script editor's read-only IL tab ──

    private static readonly ActiveTheme Light = new(new InterfaceTheme("Light", ThemeVariant.Light, false), new EditorTheme("Light+", ThemeName.LightPlus, false));
    private static readonly ActiveTheme Dark = new(new InterfaceTheme("Dark", ThemeVariant.Dark, true), new EditorTheme("Dark+", ThemeName.DarkPlus, true));

    private static (EditorsTabItemControl Tab, Window Host) OpenTab()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());
        var themeService = new Mock<IThemeService>();
        themeService.Setup(t => t.CurrentTheme).Returns(Dark);
        var vm = new EditorTabItemViewModel(null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var tab = new EditorsTabItemControl(vm, themeService.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        var host = new Window { Width = 900, Height = 600, Content = tab };
        host.Styles.Add(new RunPadTheme());
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return (tab, host);
    }

    private static void ShowIlTab(EditorsTabItemControl tab)
    {
        tab.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "OutputTabsControl").SelectedIndex = 2; // IL
        Dispatcher.UIThread.RunJobs();
    }

    private static (EditorsTabItemControl Tab, TextEditor IlOutput, Window Host) OpenIlTab()
    {
        var (tab, host) = OpenTab();
        ShowIlTab(tab);
        return (tab, tab.GetVisualDescendants().OfType<TextEditor>().Single(e => e.Name == "IlOutput"), host);
    }

    [AvaloniaFact]
    public async Task IlTab_Should_HighlightTheAttachedDocument_WithTheIlGrammar_AndFollowTheTheme()
    {
        var (tab, ilOutput, host) = OpenIlTab();
        try
        {
            Assert.Equal(IlGrammar.ScopeName, tab.IlGrammarScope); // installed when the tab joined the visual tree

            tab.AttachIlDocument(Il, tab.RunGenerationForTests);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(IlGrammar.ScopeName, tab.IlGrammarScope);
            Assert.Equal(Il, ilOutput.Text);
            Assert.True(ilOutput.IsReadOnly);
            var dark = await IlColoursAsync(ilOutput);
            Assert.Equal(3, dark.Take(3).Distinct().Count());

            tab.ApplyTheme(Light);
            var light = await SettledColorAsync(ilOutput, Opcode, c => c is { } color && color != dark[0]);
            Assert.NotEqual(dark[0], light);
            Assert.Equal(IlGrammar.ScopeName, tab.IlGrammarScope);
            Assert.Equal(Il, ilOutput.Text); // recoloured, not reloaded
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void IlTab_ShownAfterTheIlArrived_Should_BeHighlightedWhenItIsFirstShown()
    {
        var (tab, host) = OpenTab();
        try
        {
            tab.AttachIlDocument(Il, tab.RunGenerationForTests);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(tab.IlGrammarScope); // the tab's content is not in the visual tree yet

            ShowIlTab(tab);

            Assert.Equal(IlGrammar.ScopeName, tab.IlGrammarScope);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task LargeIl_Should_BeAttachedOnceOnTheUiThread_AndStayHighlighted()
    {
        var (tab, ilOutput, host) = OpenIlTab();
        try
        {
            var il = string.Concat(Enumerable.Repeat(Il, 50_000));
            var documents = 0;
            ilOutput.DocumentChanged += (_, _) => documents++;

            await Task.Run(() => tab.AttachIlDocument(il, tab.RunGenerationForTests), TestContext.Current.CancellationToken);
            Assert.Equal(0, documents); // nothing touched the editor from the worker
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, documents);
            Assert.Equal(100_001, ilOutput.Document.LineCount);
            Assert.Equal(IlGrammar.ScopeName, tab.IlGrammarScope);
            Assert.True(ilOutput.IsReadOnly);
            Assert.InRange(ilOutput.TextArea.TextView.VisualLines.Count, 1, 200);
            Assert.Equal(3, (await IlColoursAsync(ilOutput)).Take(3).Distinct().Count());
            ilOutput.Select(0, 7);
            Assert.Equal("IL_0000", ilOutput.SelectedText);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task AStaleRunsIl_Should_NotReplaceTheNewerDocument_OrDisturbTheHighlighting()
    {
        var (tab, ilOutput, host) = OpenIlTab();
        try
        {
            tab.AttachIlDocument(Il, tab.RunGenerationForTests);
            Dispatcher.UIThread.RunJobs();
            var current = ilOutput.Document;
            var scope = tab.IlGrammarScope;

            await Task.Run(() => tab.AttachIlDocument("// from an older run\n", tab.RunGenerationForTests - 1), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(current, ilOutput.Document);
            Assert.Equal(Il, ilOutput.Text);
            Assert.Equal(scope, tab.IlGrammarScope);
            Assert.Equal(3, (await IlColoursAsync(ilOutput)).Take(3).Distinct().Count());
        }
        finally { host.Close(); }
    }
}
