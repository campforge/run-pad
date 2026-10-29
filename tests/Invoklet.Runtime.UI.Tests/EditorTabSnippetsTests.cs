using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Snippets moved from EditorTabItemControl (UI layer) to ILanguageHostFactory.Snippets
// (Invoklet.Languages.CSharp/Invoklet.Languages.VB). These tests use the real
// LanguageHostRegistry/CSharpLanguageHostFactory/VbLanguageHostFactory (not mocked) to prove
// snippets actually flow from the language plugin into the editor for the tab's own language,
// rather than always being the hardcoded C# set.
public class EditorTabSnippetsTests
{
    [AvaloniaFact]
    public async Task Editor_Should_GetCSharpSnippets_When_TabLanguageIsCSharp()
    {
        var ctrl = await OpenTabAsync(RpadLanguage.CSharp);
        var snippets = ctrl.State.Editor.Snippets;

        Assert.NotEmpty(snippets);
        Assert.True(snippets.TryGetValue("for", out var forTemplate));
        Assert.Contains("for (int i", forTemplate);
        Assert.True(snippets.ContainsKey("class"));
    }

    [AvaloniaFact]
    public async Task Editor_Should_GetNoSnippets_When_TabLanguageIsVb()
    {
        // VbLanguageHostFactory defines no snippets yet — a VB tab must not silently inherit the
        // hardcoded C# ones (the bug this refactor fixes).
        var ctrl = await OpenTabAsync(RpadLanguage.Vb);

        Assert.Empty(ctrl.State.Editor.Snippets);
    }

    // The editor's lexical/syntactic profile (triggers, brace pairs, indentation) used
    // to be a single hardcoded C#-shaped set of calls in EditorTabItemControl.SetupEditor, applied to
    // every language including VB — now it flows from ILanguageHostFactory.EditorProfile, the same
    // way Snippets already does (tests above).
    [AvaloniaFact]
    public async Task Editor_Should_GetCSharpEditorProfile_When_TabLanguageIsCSharp()
    {
        var ctrl = await OpenTabAsync(RpadLanguage.CSharp);
        var editor = ctrl.State.Editor;

        Assert.Contains('<', editor.ContextualTriggers); // generic type arguments, e.g. "List<"
        Assert.Contains("new", editor.WordTriggers);
        Assert.True(editor.BracePairs.ContainsKey('{'));
        // '{' is matched but no longer auto-closed; '[' still is, '(' never was
        Assert.Equal(new[] { '[' }, editor.AutoCloseBraces);
        Assert.Equal("//", editor.LineCommentPrefix);
        Assert.Equal("prop", editor.MemberTemplateSnippet);
        Assert.True(editor.Snippets.ContainsKey("prop"));
        Assert.NotNull(editor.MemberTemplatePositionProvider);
    }

    [AvaloniaFact]
    public async Task Editor_Should_GetVbEditorProfile_When_TabLanguageIsVb()
    {
        var ctrl = await OpenTabAsync(RpadLanguage.Vb);
        var editor = ctrl.State.Editor;

        // VB has no bare '<' generics trigger (uses "Of") and no brace-delimited blocks — a VB tab
        // must not silently inherit C#'s profile (a bug this guards against).
        Assert.DoesNotContain('<', editor.ContextualTriggers);
        Assert.False(editor.BracePairs.ContainsKey('{'));
        Assert.Equal(new[] { '[' }, editor.AutoCloseBraces); // unchanged for VB
        Assert.Equal("'", editor.LineCommentPrefix);
        Assert.Null(editor.MemberTemplateSnippet);

        // VB is case-insensitive (VbLanguageHostFactory.EditorProfile builds WordTriggers with
        // StringComparer.OrdinalIgnoreCase) — only "New" was ever added, so finding differently-cased
        // "new" here only works if ApplyEditorLanguageProfile preserved that comparer instead of
        // Add()-ing entries one at a time into a fresh default-comparer HashSet. Same-cased
        // assertions (e.g. Assert.Contains("New", ...)) can't catch this — an
        // exact match passes regardless of comparer.
        Assert.Contains("new", editor.WordTriggers);
    }

    // EditorTabItemControl.ApplyEditorLanguageProfile used to switch on a
    // FoldingStrategyKind enum and instantiate `new VbFoldingStrategy()` directly by name — a
    // main-project file hardcoding a concrete, VB-specific type. It now picks a language-agnostic
    // DelegateFoldingStrategy whenever EditorLanguageProfile.FoldingRegionsProvider is set, falling
    // back to BraceFoldingStrategy when it's null (C#'s case) — these prove the right one is
    // actually selected per language, not just that the enum/property still compiles.
    [AvaloniaFact]
    public async Task Editor_Should_UseBraceFoldingStrategy_When_TabLanguageIsCSharp()
    {
        var ctrl = await OpenTabAsync(RpadLanguage.CSharp);

        Assert.IsType<BraceFoldingStrategy>(ctrl.State.Editor.FoldingStrategy);
    }

    [AvaloniaFact]
    public async Task Editor_Should_UseDelegateFoldingStrategy_When_TabLanguageIsVb()
    {
        var ctrl = await OpenTabAsync(RpadLanguage.Vb);
        var editor = ctrl.State.Editor;

        Assert.IsType<DelegateFoldingStrategy>(editor.FoldingStrategy);

        // Not just the right TYPE — the right ALGORITHM: exercise it end-to-end against a real
        // FoldingManager with VB code that only VbFoldingRegions.Compute (not the brace-based
        // default, which would find zero foldings here) can fold correctly.
        editor.Document.Text = "Public Sub Foo()\n    Console.WriteLine(1)\nEnd Sub\n";
        var foldingManager = AvaloniaEdit.Folding.FoldingManager.Install(editor.TextArea);
        editor.FoldingStrategy.UpdateFoldings(foldingManager, editor.Document);

        var folding = Assert.Single(foldingManager.AllFoldings);
        Assert.Equal(1, editor.Document.GetLineByOffset(folding.StartOffset).LineNumber);
        Assert.Equal(3, editor.Document.GetLineByOffset(folding.EndOffset).LineNumber);
    }

    private static async Task<EditorsTabItemControl> OpenTabAsync(RpadLanguage language)
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-snippets-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            // NewFile emits the matching ```{language} run fence — retargeting a parsed C# document's
            // language would leave a C# fence the parser (rightly) rejects for VB.
            File.WriteAllText(filePath, RpadWriter.NewFile(language == RpadLanguage.Vb ? "Console.WriteLine(\"Hello, RPad!\")\n" : "Console.WriteLine(\"Hello, RPad!\");\n", "Demo", language: language));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var loggerFactory = NullLoggerFactory.Instance;
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();
            var registry = new LanguageHostRegistry();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory).WithIntelliSense(() => intelliSense.Object));
            registry.Register(new VbLanguageHostFactory(loggerFactory));

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry, prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);

            var fileTabControl = editorTabs.GetVisualDescendants().OfType<TabControl>()
                .First(t => t.Name == "FileTabControl");
            var selectedTab = (TabItem)fileTabControl.SelectedItem!;
            var ctrl = (EditorsTabItemControl)selectedTab.Content!;

            return ctrl;
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
