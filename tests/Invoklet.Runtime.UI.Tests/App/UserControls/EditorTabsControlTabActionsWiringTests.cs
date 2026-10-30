using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// DuplicateCommand/OpenContainingFolderCommand now call straight into
// MainWindowViewModel (via IEditorTabActions), reading the View-layer write callback off
// tab.WriteToFile — set once by EditorsTabItemControl's own constructor (see EditorTabItemViewModel.
// WriteToFile's comment). EditorTabsControlCloseConfirmationTests already covers CloseCommand/
// SaveAsCommand end-to-end through the same wiring; these cover the other two commands, proving
// WriteToFile really does reach MainWindowViewModel through a genuine EditorsTabItemControl, not a
// mock standing in for it.
public class EditorTabsControlTabActionsWiringTests
{
    private static (EditorTabsControl EditorTabs, MainWindowViewModel MainVm, Mock<IFileSystemService> FileSystem, string RootFolder)
        CreateHarness()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-tabactions-wiring-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

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
            theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
            buildCache.Object);

        var host = new Window { Content = editorTabs };
        host.Show();
        editorTabs.Attach(mainVm);

        return (editorTabs, mainVm, fileSystem, rootFolder);
    }

    private static async Task<EditorsTabItemControl> GetSoleControlAsync(EditorTabsControl editorTabs)
    {
        for (var i = 0; i < 100; i++)
        {
            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().FirstOrDefault();
            if (ctrl is not null) return ctrl;
            await Task.Delay(20);
        }
        throw new TimeoutException("No EditorsTabItemControl was realized in time.");
    }

    [AvaloniaFact]
    public async Task DuplicateCommand_Should_WriteACopyNextToTheOriginal_ThroughTheRealEditorsTabItemControl()
    {
        var (editorTabs, mainVm, _, rootFolder) = CreateHarness();
        try
        {
            var original = Path.Combine(rootFolder, "Query 1.inv");
            File.WriteAllText(original, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Query 1"));
            mainVm.OpenTab(original);
            await GetSoleControlAsync(editorTabs);
            var tab = Assert.Single(mainVm.Tabs);
            Assert.NotNull(tab.WriteToFile); // the real control wired itself in by now

            tab.DuplicateCommand.Execute().Subscribe();

            var expectedCopyPath = Path.Combine(rootFolder, "Query 1 copy.inv");
            await TestWait.WhileAsync(() => !File.Exists(expectedCopyPath));

            Assert.True(File.Exists(expectedCopyPath));
            Assert.Contains(mainVm.Tabs, t => t.FilePath == expectedCopyPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task OpenContainingFolderCommand_Should_CallFileSystemServiceOpenFolder_ForTheTabsPath()
    {
        var (editorTabs, mainVm, fileSystem, rootFolder) = CreateHarness();
        try
        {
            var filePath = Path.Combine(rootFolder, "demo.inv");
            File.WriteAllText(filePath, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));
            mainVm.OpenTab(filePath);
            await GetSoleControlAsync(editorTabs);
            var tab = Assert.Single(mainVm.Tabs);

            tab.OpenContainingFolderCommand.Execute().Subscribe();

            fileSystem.Verify(f => f.OpenFolder(filePath), Times.Once);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
