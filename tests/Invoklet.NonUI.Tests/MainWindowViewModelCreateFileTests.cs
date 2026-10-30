using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// File tree had no "create new file" command — only the deferred, disk-untouching
// File → New Invoklet File menu (see MainWindowViewModelNewFileTests.cs). CreateFileAsync resolves the
// target directory the same way CreateFolderAsync does (see MainWindowViewModelCreateFolderTests.cs
// for the three directory-resolution cases, not re-tested here to avoid duplication), delegates the
// actual file creation to IFileSystemService.CreateNewFile, and — unlike "New Folder" — also opens
// the result as a tab right away.
public class MainWindowViewModelCreateFileTests
{
    private static MainWindowViewModel CreateViewModel(Mock<IFileSystemService> fileSystem)
    {
        fileSystem.Setup(f => f.Folder).Returns("/root");
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());

        return new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
    }

    [Fact]
    public async Task CreateFileAsync_Should_UseTheRoot_WhenNothingIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFile("/root", MdRunLanguage.CSharp, MdRunKind.Statements, It.IsAny<string>()))
            .Returns("/root/Query 1.inv");
        var vm = CreateViewModel(fileSystem);

        var result = await vm.CreateFileAsync(null, MdRunLanguage.CSharp, MdRunKind.Statements);

        Assert.Equal("/root/Query 1.inv", result);
        fileSystem.Verify(f => f.CreateNewFile("/root", MdRunLanguage.CSharp, MdRunKind.Statements, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CreateFileAsync_Should_CreateInsideTheSelectedFolder_WhenAFolderIsSelected()
    {
        var fileSystem = new Mock<IFileSystemService>();
        Directory.CreateDirectory("/tmp/Invoklet-createfile-test/Sub");
        try
        {
            fileSystem.Setup(f => f.CreateNewFile(
                    "/tmp/Invoklet-createfile-test/Sub", MdRunLanguage.Vb, MdRunKind.Program, It.IsAny<string>()))
                .Returns("/tmp/Invoklet-createfile-test/Sub/Query 1.inv");
            var vm = CreateViewModel(fileSystem);

            var result = await vm.CreateFileAsync(
                "/tmp/Invoklet-createfile-test/Sub", MdRunLanguage.Vb, MdRunKind.Program);

            Assert.Equal("/tmp/Invoklet-createfile-test/Sub/Query 1.inv", result);
        }
        finally { Directory.Delete("/tmp/Invoklet-createfile-test", recursive: true); }
    }

    [Fact]
    public async Task CreateFileAsync_Should_PassTheRequestedLanguageAndKind_ThroughToTheFileSystemService()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFile("/root", MdRunLanguage.Vb, MdRunKind.Expression, It.IsAny<string>()))
            .Returns("/root/Query 1.inv");
        var vm = CreateViewModel(fileSystem);

        await vm.CreateFileAsync(null, MdRunLanguage.Vb, MdRunKind.Expression);

        fileSystem.Verify(f => f.CreateNewFile("/root", MdRunLanguage.Vb, MdRunKind.Expression, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CreateFileAsync_Should_RefreshTheTree_AfterCreating()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFile("/root", MdRunLanguage.CSharp, MdRunKind.Statements, It.IsAny<string>()))
            .Returns("/root/Query 1.inv");
        var vm = CreateViewModel(fileSystem);

        await vm.CreateFileAsync(null, MdRunLanguage.CSharp, MdRunKind.Statements);

        fileSystem.Verify(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CreateFileAsync_Should_OpenTheNewFile_AsTheActiveTab()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.CreateNewFile("/root", MdRunLanguage.CSharp, MdRunKind.Statements, It.IsAny<string>()))
            .Returns("/root/Query 1.inv");
        var vm = CreateViewModel(fileSystem);

        await vm.CreateFileAsync(null, MdRunLanguage.CSharp, MdRunKind.Statements);

        Assert.Equal(Path.GetFullPath("/root/Query 1.inv"), vm.ActiveTab?.FilePath);
    }
}
