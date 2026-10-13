using Moq;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// NewRpadFile used to write a real (sample-boilerplate-filled)
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
    public void NewRpadFile_Should_NotTouchDisk()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-newfile-test").FullName;
        try
        {
            var (vm, fileSystem) = CreateViewModel(rootFolder);

            vm.NewRpadFile();

            fileSystem.Verify(
                f => f.CreateNewFile(It.IsAny<string>(), It.IsAny<RpadLanguage>(), It.IsAny<RpadKind>()),
                Times.Never);
            Assert.Empty(Directory.GetFiles(rootFolder));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_OpenAnUntitledTab_DefaultingToCSharpStatements()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewRpadFile();

            var tab = Assert.Single(vm.Tabs, t => t.FilePath is null);
            Assert.Equal(RpadLanguage.CSharp, tab.NewFileLanguage);
            Assert.Equal(RpadKind.Statements, tab.NewFileKind);
            Assert.False(tab.HasFilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_CarryTheRequestedLanguageAndKind_OnTheNewTab()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewRpadFile(RpadLanguage.Vb, RpadKind.Program);

            var tab = Assert.Single(vm.Tabs, t => t.FilePath is null && t.NewFileLanguage == RpadLanguage.Vb);
            Assert.Equal(RpadKind.Program, tab.NewFileKind);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_MakeTheNewTabActive()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-newfile-test").FullName;
        try
        {
            var (vm, _) = CreateViewModel(rootFolder);

            vm.NewRpadFile(RpadLanguage.Vb);

            Assert.Same(vm.Tabs[^1], vm.ActiveTab);
            Assert.Null(vm.ActiveTab!.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
