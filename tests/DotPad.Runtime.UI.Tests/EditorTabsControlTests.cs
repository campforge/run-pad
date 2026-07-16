using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

public class EditorTabsControlTests
{
    [AvaloniaFact]
    public async Task ActiveTab_Should_StaySetToTheNewTab_When_OpeningAFileClosesTheInitialBlankTab()
    {
        // Arrange: OpenTab(path) adds the new tab, activates it, then closes the now-redundant
        // initial blank tab — all synchronously — while the new tab's own control is still being
        // built off the UI thread (EditorTabsControl.CreateControlAsync). Closing the blank tab
        // removes its TabItem from the TabControl before the new tab's TabItem exists, which
        // transiently leaves the TabControl with no SelectedItem.
        var tmpDir = Directory.CreateTempSubdirectory("dotpad-tabs-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));

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

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, registry.Object, prefs.Object, () => intelliSense.Object,
                new CSharpReferenceService(), buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            // Act
            mainVm.OpenTab(filePath);
            for (var i = 0; i < 100 && mainVm.ActiveTab?.FrontMatter is null; i++)
                await Task.Delay(20);

            // Assert
            Assert.NotNull(mainVm.ActiveTab);
            Assert.Equal(filePath, mainVm.ActiveTab.FilePath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ANALYSIS.md TODO #66: EditorTabItemControl now pushes the tab's front-matter `kind:` into its
    // IntelliSense instance (mirroring the existing RefreshIntelliSensePackages/RefreshIntelliSenseUsings
    // pattern) so CSharpIntelliSenseService's live-analysis document can mirror the same kind-specific
    // transform the compiler applies (e.g. Program's implicit `Main();` call) — without this wiring,
    // the service-level fix (CSharpIntelliSenseService.UpdateKind) is never actually reached from a
    // real opened file.
    [AvaloniaFact]
    public async Task OpenTab_Should_PushTheFileKindIntoIntelliSense_ForAProgramKindFile()
    {
        var tmpDir = Directory.CreateTempSubdirectory("dotpad-tabs-kind-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo", kind: RpadKind.Program));

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

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, registry.Object, prefs.Object, () => intelliSense.Object,
                new CSharpReferenceService(), buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            mainVm.OpenTab(filePath);
            for (var i = 0; i < 100 && mainVm.ActiveTab?.FrontMatter is null; i++)
                await Task.Delay(20);

            intelliSense.Verify(x => x.UpdateKind(RpadKind.Program), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
