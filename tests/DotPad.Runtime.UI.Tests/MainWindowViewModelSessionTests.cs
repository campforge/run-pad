using DotPad.Contracts;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md, "Жизненный цикл вкладок (остаток)": the app always started with a single blank tab —
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
        fileSystem.Setup(f => f.GetFilesTree()).Returns([]);

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
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
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
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.rpad");
            var pathB = Path.Combine(rootFolder, "B.rpad");
            var pathC = Path.Combine(rootFolder, "C.rpad");
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
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.rpad");
            var pathB = Path.Combine(rootFolder, "B.rpad");
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
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var stillThere = Path.Combine(rootFolder, "StillThere.rpad");
            var deletedSincelastSession = Path.Combine(rootFolder, "Deleted.rpad");
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
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var (vm, session) = CreateViewModel(rootFolder);
            session.OpenTabs = [Path.Combine(rootFolder, "Gone1.rpad"), Path.Combine(rootFolder, "Gone2.rpad")];

            vm.RestoreSession();

            var tab = Assert.Single(vm.Tabs);
            Assert.Null(tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void OpenTab_Should_PersistTheNewTabIntoSession()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var path = Path.Combine(rootFolder, "Query.rpad");
            File.WriteAllText(path, "");
            var (vm, session) = CreateViewModel(rootFolder);

            vm.OpenTab(path);

            Assert.Equal([path], session.OpenTabs);
            Assert.Equal(path, session.ActiveTabPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_NotAddAnUntitledTab_ToTheSessionsPathList()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var (vm, session) = CreateViewModel(rootFolder);

            vm.NewRpadFile();

            Assert.Empty(session.OpenTabs);
            Assert.Null(session.ActiveTabPath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void ClosingANonActiveTab_Should_StillPersistTheUpdatedSession()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-session-test").FullName;
        try
        {
            var pathA = Path.Combine(rootFolder, "A.rpad");
            var pathB = Path.Combine(rootFolder, "B.rpad");
            File.WriteAllText(pathA, "");
            File.WriteAllText(pathB, "");
            var (vm, session) = CreateViewModel(rootFolder);

            vm.OpenTab(pathA);
            vm.OpenTab(pathB);
            Assert.Equal(pathB, vm.ActiveTab?.FilePath); // B is active; closing A below doesn't touch it

            var tabA = vm.Tabs.Single(t => t.FilePath == pathA);
            tabA.CloseCommand.Execute().Subscribe();

            Assert.Equal([pathB], session.OpenTabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
