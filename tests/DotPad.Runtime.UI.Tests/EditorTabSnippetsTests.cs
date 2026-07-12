using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.VB;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// Snippets moved from EditorTabItemControl (UI layer) to ILanguageHostFactory.Snippets
// (DotPad.Languages.CSharp/DotPad.Languages.VB) — see TODO.md #41. These tests use the real
// LanguageHostRegistry/CSharpLanguageHostFactory/VbLanguageHostFactory (not mocked) to prove
// snippets actually flow from the language plugin into the editor for the tab's own language,
// rather than always being the hardcoded C# set.
public class EditorTabSnippetsTests
{
    [AvaloniaFact]
    public async Task Editor_Should_GetCSharpSnippets_When_TabLanguageIsCSharp()
    {
        var snippets = await OpenTabAndGetSnippetsAsync(RpadLanguage.CSharp);

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
        var snippets = await OpenTabAndGetSnippetsAsync(RpadLanguage.Vb);

        Assert.Empty(snippets);
    }

    private static async Task<Dictionary<string, string>> OpenTabAndGetSnippetsAsync(RpadLanguage language)
    {
        var tmpDir = Directory.CreateTempSubdirectory("dotpad-snippets-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            var doc = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
            doc.FrontMatter.Language = language;
            File.WriteAllText(filePath, RpadWriter.Write(doc));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTree()).Returns([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var loggerFactory = NullLoggerFactory.Instance;
            var registry = new LanguageHostRegistry();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory));
            registry.Register(new VbLanguageHostFactory(loggerFactory));

            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object);

            var editorTabs = new EditorTabsControl(
                theme.Object, registry, prefs.Object, () => intelliSense.Object,
                new CSharpReferenceService(), buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            mainVm.OpenTab(filePath);
            for (var i = 0; i < 100 && mainVm.ActiveTab?.FrontMatter is null; i++)
                await Task.Delay(20);

            var fileTabControl = editorTabs.GetVisualDescendants().OfType<TabControl>()
                .First(t => t.Name == "FileTabControl");
            var selectedTab = (TabItem)fileTabControl.SelectedItem!;
            var ctrl = (EditorsTabItemControl)selectedTab.Content!;

            return new Dictionary<string, string>(ctrl.State.Editor.Snippets);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
