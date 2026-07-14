using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #49 item 2 — NewRpadFile always called RpadWriter.NewFile with no language, so the "new
// file" button/menu could only ever produce a C# script; a .vb file could only be made by
// hand-editing an existing C# file's front matter afterward.
public class MainWindowViewModelNewFileTests
{
    private static (MainWindowViewModel Vm, string Path) CreateViewModelWithPendingNewFile(string rootFolder)
    {
        var path = Path.Combine(rootFolder, "Query 1.rpad");
        File.WriteAllText(path, string.Empty); // mirrors IFileSystemService.CreateNewFile's own behavior

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTree()).Returns([]);
        fileSystem.Setup(f => f.CreateNewFile()).Returns(path);

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object);

        return (vm, path);
    }

    [Fact]
    public void NewRpadFile_Should_DefaultToCSharp_WhenNoLanguageIsSpecified()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-newfile-test").FullName;
        try
        {
            var (vm, path) = CreateViewModelWithPendingNewFile(rootFolder);

            vm.NewRpadFile();

            var parsed = RpadParser.TryParse(File.ReadAllText(path));
            Assert.NotNull(parsed);
            Assert.Equal(RpadLanguage.CSharp, parsed!.FrontMatter.Language);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_WriteAVbFile_WhenVbLanguageIsRequested()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-newfile-test").FullName;
        try
        {
            var (vm, path) = CreateViewModelWithPendingNewFile(rootFolder);

            vm.NewRpadFile(RpadLanguage.Vb);

            var parsed = RpadParser.TryParse(File.ReadAllText(path));
            Assert.NotNull(parsed);
            Assert.Equal(RpadLanguage.Vb, parsed!.FrontMatter.Language);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void NewRpadFile_Should_OpenATabForTheNewFile()
    {
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-newfile-test").FullName;
        try
        {
            var (vm, path) = CreateViewModelWithPendingNewFile(rootFolder);

            vm.NewRpadFile(RpadLanguage.Vb);

            Assert.Contains(vm.Tabs, t => t.FilePath == path);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
