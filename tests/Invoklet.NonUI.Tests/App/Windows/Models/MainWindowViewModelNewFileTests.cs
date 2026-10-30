using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

// NewMdRunFile used to write a real (sample-boilerplate-filled)
// file to disk immediately via IFileSystemService.CreateNewFile — a script only started existing on
// disk (and in the file tree) once the user explicitly saved it. language/kind are now carried on
// the tab itself (EditorTabItemViewModel.NewFileLanguage/NewFileKind) since there's no front matter
// on disk yet to read them back from.
public class MainWindowViewModelNewFileTests
{
    private static (MainWindowViewModel Vm, Mock<IFileSystemService> FileSystem) CreateViewModel(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, fileSystem);
    }

    [Fact]
    public void NewMdRunFile_Should_NotTouchDisk()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-newfile-test").FullName;
        try
        {
            var (vm, fileSystem) = CreateViewModel(rootFolder);

            vm.NewMdRunFile();

            fileSystem.Verify(
                f => f.CreateNewFile(It.IsAny<string>(), It.IsAny<MdRunLanguage>(), It.IsAny<MdRunKind>(), It.IsAny<string>()),
                Times.Never);
            Assert.Empty(Directory.GetFiles(rootFolder));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewMdRunFile_Should_OpenAnUntitledTab_DefaultingToCSharpStatements()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewMdRunFile();

            var tab = Assert.Single(vm.Tabs, t => t.FilePath is null);
            Assert.Equal(MdRunLanguage.CSharp, tab.NewFileLanguage);
            Assert.Equal(MdRunKind.Statements, tab.NewFileKind);
            Assert.False(tab.HasFilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewMdRunFile_Should_CarryTheRequestedLanguageAndKind_OnTheNewTab()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewMdRunFile(MdRunLanguage.Vb, MdRunKind.Program);

            var tab = Assert.Single(vm.Tabs, t => t.FilePath is null && t.NewFileLanguage == MdRunLanguage.Vb);
            Assert.Equal(MdRunKind.Program, tab.NewFileKind);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewMdRunFile_Should_MakeTheNewTabActive()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewMdRunFile(MdRunLanguage.Vb);

            Assert.Same(vm.Tabs[^1], vm.ActiveTab);
            Assert.Null(vm.ActiveTab!.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewMdRunFile_Should_UseIncrementingUntitledNames()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewMdRunFile();
            vm.NewMdRunFile();

            Assert.Equal(["Untitled1", "Untitled2"], vm.Tabs.Select(t => t.FileName));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void Recovery_Should_DropPristineUntitledTabs_ButKeepDirtyAndImportedTabs()
    {
        static RecoveryTab Tab(bool dirty = false, string? path = null, string? original = null) => new()
        {
            EditorText = "",
            IsDirty = dirty,
            FilePath = path,
            OriginalPath = original,
        };

        Assert.False(MainWindowViewModel.ShouldKeepRecoveryTab(Tab()));
        Assert.True(MainWindowViewModel.ShouldKeepRecoveryTab(Tab(dirty: true)));
        Assert.True(MainWindowViewModel.ShouldKeepRecoveryTab(Tab(path: "/tmp/a.inv")));
        Assert.True(MainWindowViewModel.ShouldKeepRecoveryTab(Tab(original: "/tmp/a.inv")));
    }
}
