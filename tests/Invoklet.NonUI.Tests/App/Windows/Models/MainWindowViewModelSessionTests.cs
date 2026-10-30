using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

// The app always started with a single blank tab —
// nothing about which files were open (or their order) survived a restart. RestoreSession replaces
// that unconditional EditorTabsControl.Attach → OpenTab(null) call; PersistSession keeps
// IPreferencesService.Session in sync with Tabs/ActiveTab on every open/close/switch so there's
// always something current to restore from on the next launch.
public class MainWindowViewModelSessionTests
{
    private static (MainWindowViewModel Vm, SessionSettings Session) CreateViewModel(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var session = new SessionSettings();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Session).Returns(session);

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, session);
    }

    [Fact]
    public void RestoreSession_Should_OpenABlankTab_WhenNothingWasSaved()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.RestoreSession();

            var tab = Assert.Single(vm.Tabs);
            Assert.Null(tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void RestoreSession_Should_ReopenSavedTabs_InTheirSavedOrder()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.inv");
            var pathB = Path.Combine(rootFolder, "B.inv");
            var pathC = Path.Combine(rootFolder, "C.inv");
            File.WriteAllText(pathA, "");
            File.WriteAllText(pathB, "");
            File.WriteAllText(pathC, "");

            var (vm, session) = CreateViewModel(rootFolder);
            session.OpenTabs = [pathC, pathA, pathB];

            vm.RestoreSession();

            Assert.Equal([pathC, pathA, pathB], vm.Tabs.Select(t => t.FilePath));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void RestoreSession_Should_ReselectTheSavedActiveTab()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.inv");
            var pathB = Path.Combine(rootFolder, "B.inv");
            File.WriteAllText(pathA, "");
            File.WriteAllText(pathB, "");

            var (vm, session) = CreateViewModel(rootFolder);
            session.OpenTabs = [pathA, pathB];
            session.ActiveTabPath = pathA;

            vm.RestoreSession();

            // Without this, the last-opened tab (pathB, per OpenTab/AddTab's own "select what was
            // just added" behavior) would win instead of the one actually active last session.
            Assert.Equal(pathA, vm.ActiveTab?.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void RestoreSession_Should_SkipSavedPaths_ThatNoLongerExistOnDisk()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var stillThere = Path.Combine(rootFolder, "StillThere.inv");
            var deletedSincelastSession = Path.Combine(rootFolder, "Deleted.inv");
            File.WriteAllText(stillThere, "");

            var (vm, session) = CreateViewModel(rootFolder);
            session.OpenTabs = [deletedSincelastSession, stillThere];

            vm.RestoreSession();

            var tab = Assert.Single(vm.Tabs);
            Assert.Equal(stillThere, tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void RestoreSession_Should_FallBackToABlankTab_WhenEverySavedPathIsGone()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var (vm, session) = CreateViewModel(rootFolder);
            session.OpenTabs = [Path.Combine(rootFolder, "Gone1.inv"), Path.Combine(rootFolder, "Gone2.inv")];

            vm.RestoreSession();

            var tab = Assert.Single(vm.Tabs);
            Assert.Null(tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void OpenTab_Should_PersistTheNewTabIntoSession()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var path = Path.Combine(rootFolder, "Query.inv");
            File.WriteAllText(path, "");
            var (vm, session) = CreateViewModel(rootFolder);

            vm.OpenTab(path);

            Assert.Equal([path], session.OpenTabs);
            Assert.Equal(path, session.ActiveTabPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewMdRunFile_Should_NotAddAnUntitledTab_ToTheSessionsPathList()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var (vm, session) = CreateViewModel(rootFolder);

            vm.NewMdRunFile();

            Assert.Empty(session.OpenTabs);
            Assert.Null(session.ActiveTabPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void ClosingANonActiveTab_Should_StillPersistTheUpdatedSession()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.inv");
            var pathB = Path.Combine(rootFolder, "B.inv");
            File.WriteAllText(pathA, "");
            File.WriteAllText(pathB, "");
            var (vm, session) = CreateViewModel(rootFolder);

            vm.OpenTab(pathA);
            vm.OpenTab(pathB);
            Assert.Equal(pathB, vm.ActiveTab?.FilePath); // B is active; closing A below doesn't touch it

            // CloseCommand itself isn't subscribed at this (ViewModel-only) level any more — closing
            // now needs EditorsTabItemControl.WriteFile for the unsaved-changes confirmation dialog's
            // "Save" choice, so the real subscription lives in EditorTabsControl. CloseTab is the
            // lower-level primitive that subscription calls once a close is actually confirmed —
            // exactly what this test means to exercise (see EditorTabsControlTests for the
            // confirmation flow itself, which does go through a real EditorTabsControl).
            var tabA = vm.Tabs.Single(t => t.FilePath == pathA);
            vm.CloseTab(tabA);

            Assert.Equal([pathB], session.OpenTabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
