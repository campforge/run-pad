using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Switching the theme recolours the shown script at once and every other open script when it is next
// shown, before it is drawn — keywords and strings (TextMate) and IntelliSense's semantic colours —
// without typing, reopening or redrawing by hand. A script that had never been shown yet used to be
// coloured, when first opened, with the palette of the moment its tab was created. Colours are read
// from the editor's rendered visual lines, compared with a fresh editor in the expected palette.
public class EditorThemeRefreshTests : IDisposable
{
    private const string Code = "var count = 1;\nclass Greeter { }\nConsole.WriteLine(\"text\" + count);\n";

    private static readonly ActiveTheme Light = new(new InterfaceTheme("Light", ThemeVariant.Light, false), new EditorTheme("Light+", ThemeName.LightPlus, false));
    private static readonly ActiveTheme Dark = new(new InterfaceTheme("Dark", ThemeVariant.Dark, true), new EditorTheme("Dark+", ThemeName.DarkPlus, true));

    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-theme-refresh").FullName;
    private readonly Mock<IThemeService> _theme = new();
    private readonly Mock<IIntelliSenseService> _intelliSense = new();
    private ActiveTheme _current = Light;

    public EditorThemeRefreshTests() => _theme.Setup(t => t.CurrentTheme).Returns(() => _current);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Script(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, RpadWriter.NewFile(Code, name));
        return path;
    }

    private (EditorTabsControl Tabs, MainWindowViewModel Shell, Window Host) Open()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var shell = new MainWindowViewModel([], fileSystem.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(_theme.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => _intelliSense.Object),
            prefs.Object, new Mock<IBuildCacheService>().Object);
        var host = new Window { Width = 900, Height = 700, Content = tabs };
        host.Show();
        tabs.Attach(shell);
        return (tabs, shell, host);
    }

    private void SwitchTo(ActiveTheme theme)
    {
        _current = theme;
        _theme.Raise(t => t.ThemeChanged += null, theme);
        Dispatcher.UIThread.RunJobs();
    }

    // What IntelliSense would report for the script: "Greeter" is a class name.
    private void PublishSemanticTokens() =>
        _intelliSense.Raise(s => s.SemanticTokensChanged += null, Code,
            (IReadOnlyList<ISemanticToken>)[new Token(Code.IndexOf("Greeter", StringComparison.Ordinal), "Greeter".Length, SemanticTokenKind.ClassName)]);

    private sealed record Token(int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

    private static Color? RenderedColor(CodeTextEditor editor, int offset)
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

    private static async Task<Color?> WaitForColorAsync(CodeTextEditor editor, int offset, Color expected)
    {
        Color? seen = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            seen = RenderedColor(editor, offset);
            if (seen == expected) return seen;
            await Task.Delay(25);
        }
        return seen;
    }

    // A freshly created editor in the palette: the colour a keyword or a string should have.
    private static async Task<Color> ReferenceAsync(ThemeName palette, int offset)
    {
        var editor = new CodeTextEditor { Document = new TextDocument(Code) };
        var window = new Window { Width = 900, Height = 700, Content = editor };
        window.Show();
        editor.SetLanguage(palette, ".cs");
        try
        {
            Color? last = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                var c = RenderedColor(editor, offset);
                if (c is { } color && color != Colors.Black && c == last) return color;
                last = c;
                await Task.Delay(100);
            }
            return last ?? throw new InvalidOperationException("the reference editor never coloured the text");
        }
        finally { window.Close(); }
    }

    private static readonly int Keyword = Code.IndexOf("var", StringComparison.Ordinal);
    private static readonly int StringLiteral = Code.IndexOf("\"text\"", StringComparison.Ordinal) + 1;
    private static readonly int ClassName = Code.IndexOf("Greeter", StringComparison.Ordinal);

    private static Color Semantic(bool dark) => ((ISolidColorBrush)SemanticTokenBrushes.GetBrush(SemanticTokenKind.ClassName, dark)!).Color;

    private static async Task<EditorsTabItemControl> ShownAsync(EditorTabsControl tabs, EditorTabItemViewModel vm)
    {
        await TestWait.WhileAsync(() => tabs.ControlForTests(vm) is null);
        return tabs.ControlForTests(vm)!;
    }

    [AvaloniaFact]
    public async Task TheShownScript_Should_TakeTheNewPalette_AtOnce()
    {
        var (lightKeyword, darkKeyword) = (await ReferenceAsync(ThemeName.LightPlus, Keyword), await ReferenceAsync(ThemeName.DarkPlus, Keyword));
        var darkString = await ReferenceAsync(ThemeName.DarkPlus, StringLiteral);
        Assert.NotEqual(lightKeyword, darkKeyword);
        var (tabs, shell, host) = Open();
        try
        {
            shell.OpenTab(Script("a.rpad"));
            var editor = (await ShownAsync(tabs, shell.ActiveTab!)).State.Editor;
            Assert.Equal(lightKeyword, await WaitForColorAsync(editor, Keyword, lightKeyword));
            PublishSemanticTokens();
            Assert.Equal(Semantic(dark: false), await WaitForColorAsync(editor, ClassName, Semantic(dark: false)));

            SwitchTo(Dark);

            Assert.Equal(darkKeyword, await WaitForColorAsync(editor, Keyword, darkKeyword));
            Assert.Equal(darkString, await WaitForColorAsync(editor, StringLiteral, darkString));
            Assert.Equal(Semantic(dark: true), await WaitForColorAsync(editor, ClassName, Semantic(dark: true)));
            Assert.Equal(Code, editor.Text);
        }
        finally { host.Close(); }
    }

    // A theme switch recolours the editor; it never recreates or replaces the tab's editor services.
    [AvaloniaFact]
    public async Task AThemeSwitch_Should_KeepTheTabsEditorServices()
    {
        var (tabs, shell, host) = Open();
        try
        {
            shell.OpenTab(Script("services.rpad"));
            var ctrl = await ShownAsync(tabs, shell.ActiveTab!);
            var services = ctrl.IntelliSenseForTests;
            Assert.Same(_intelliSense.Object, services);
            // Every tab here shares this one fake: the initial blank tab disposed it when it closed.
            var disposals = _intelliSense.Invocations.Count(i => i.Method.Name == nameof(IDisposable.Dispose));

            SwitchTo(Dark);
            SwitchTo(Light);

            Assert.Same(services, ctrl.IntelliSenseForTests);
            Assert.Equal(disposals, _intelliSense.Invocations.Count(i => i.Method.Name == nameof(IDisposable.Dispose)));
        }
        finally { host.Close(); }
    }

    // Shown once, then hidden behind another tab while the theme changes several times: nothing is
    // applied to it while hidden; when shown again it has the latest palette, lexical and semantic.
    [AvaloniaFact]
    public async Task AHiddenScript_Should_ApplyOnlyTheLatestTheme_WhenShownAgain()
    {
        var darkKeyword = await ReferenceAsync(ThemeName.DarkPlus, Keyword);
        var lightKeyword = await ReferenceAsync(ThemeName.LightPlus, Keyword);
        var (tabs, shell, host) = Open();
        try
        {
            shell.OpenTab(Script("a.rpad"));
            var first = shell.ActiveTab!;
            var a = await ShownAsync(tabs, first);
            Assert.Equal(lightKeyword, await WaitForColorAsync(a.State.Editor, Keyword, lightKeyword));
            PublishSemanticTokens();
            shell.OpenTab(Script("b.rpad"));
            var b = await ShownAsync(tabs, shell.ActiveTab!);
            await TestWait.WhileAsync(() => TopLevel.GetTopLevel(a) is not null);
            Assert.Null(TopLevel.GetTopLevel(a));

            SwitchTo(Dark);
            SwitchTo(Light);
            SwitchTo(Dark);

            Assert.Same(Dark, a.PendingThemeForTests); // waiting, not applied to the hidden editor
            Assert.Null(b.PendingThemeForTests);
            Assert.Equal(darkKeyword, await WaitForColorAsync(b.State.Editor, Keyword, darkKeyword));

            shell.ActiveTab = first;

            Assert.Equal(darkKeyword, await WaitForColorAsync(a.State.Editor, Keyword, darkKeyword));
            Assert.Equal(Semantic(dark: true), await WaitForColorAsync(a.State.Editor, ClassName, Semantic(dark: true)));
            Assert.Null(a.PendingThemeForTests);
            Assert.Equal(Code, a.State.Editor.Text);
        }
        finally { host.Close(); }
    }

    // Created while another tab is shown and not shown before the theme changes (like a tab restored
    // at startup): its first appearance uses the palette of that moment, not the one it was created in.
    [AvaloniaFact]
    public async Task AScriptNeverShownBeforeTheSwitch_Should_OpenInTheCurrentPalette()
    {
        var darkKeyword = await ReferenceAsync(ThemeName.DarkPlus, Keyword);
        var (tabs, shell, host) = Open();
        try
        {
            shell.OpenTab(Script("a.rpad"));
            var first = shell.ActiveTab!;
            shell.OpenTab(Script("b.rpad"));
            var a = await ShownAsync(tabs, first);
            await ShownAsync(tabs, shell.ActiveTab!);
            Assert.Null(TopLevel.GetTopLevel(a)); // never in the visual tree so far

            SwitchTo(Dark);
            shell.ActiveTab = first;

            Assert.Equal(darkKeyword, await WaitForColorAsync(a.State.Editor, Keyword, darkKeyword));
        }
        finally { host.Close(); }
    }

    // ── The other windows that show code ─────────────────────────────────────

    [AvaloniaFact]
    public async Task TheTextEditorWindow_Should_TakeTheNewPalette_AtOnce()
    {
        var (lightKeyword, darkKeyword) = (await ReferenceAsync(ThemeName.LightPlus, Keyword), await ReferenceAsync(ThemeName.DarkPlus, Keyword));
        var path = Path.Combine(_root, "Program.cs");
        File.WriteAllText(path, Code);
        var viewModel = await TextEditorWindowViewModel.LoadAsync(path, new Mock<IWindowService>().Object);
        var window = new TextEditorWindow(viewModel, _theme.Object, preferences: null);
        window.Show();
        try
        {
            Assert.Equal(lightKeyword, await WaitForColorAsync(window.Editor, Keyword, lightKeyword));

            SwitchTo(Dark);

            Assert.Equal(darkKeyword, await WaitForColorAsync(window.Editor, Keyword, darkKeyword));
            Assert.Equal(Code, window.Editor.Text);
        }
        finally { window.Close(); }
    }

    // A plain-text file has no grammar: switching the theme leaves it as it is, without failing.
    [AvaloniaFact]
    public async Task APlainTextFile_Should_SurviveAThemeSwitch()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "just text\n");
        var viewModel = await TextEditorWindowViewModel.LoadAsync(path, new Mock<IWindowService>().Object);
        var window = new TextEditorWindow(viewModel, _theme.Object, preferences: null);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            SwitchTo(Dark);
            SwitchTo(Light);

            Assert.Equal("just text\n", window.Editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheAssemblyExplorer_Should_TakeTheNewPalette_AtOnce()
    {
        var (lightKeyword, darkKeyword) = (await ReferenceAsync(ThemeName.LightPlus, Keyword), await ReferenceAsync(ThemeName.DarkPlus, Keyword));
        var window = new AssemblyExplorerWindow(_theme.Object, new Mock<IWindowService>().Object, "");
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.Editor.Document.Text = Code;
            Assert.Equal(lightKeyword, await WaitForColorAsync(window.Editor, Keyword, lightKeyword));

            SwitchTo(Dark);

            Assert.Equal(darkKeyword, await WaitForColorAsync(window.Editor, Keyword, darkKeyword));
        }
        finally { window.Close(); }
    }
}
