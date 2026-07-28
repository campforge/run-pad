using Moq;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #63 "конвертер .linq → .rpad" — MainWindowViewModel.ImportLinqFileAsync, the "File →
// Import LINQPad Query..." entry point around LinqConverter (see LinqConverterTests.cs in
// RunPad.Format.RPad.Tests for the conversion logic itself, not re-tested here).
public class ImportLinqFileTests
{
    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static (MainWindowViewModel Vm, Mock<IWindowService> WindowService) CreateMainVm(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var windowService = new Mock<IWindowService>();

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: MakePrefs().Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, windowService);
    }

    private const string ValidLinq = """
        <Query Kind="Statements">
          <NuGetReference>Newtonsoft.Json</NuGetReference>
          <Namespace>System.Text</Namespace>
        </Query>

        Console.WriteLine("hi");
        """;

    [Fact]
    public async Task ImportLinqFileAsync_Should_DoNothing_WhenTheDialogIsCancelled()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-linq-import-test").FullName;
        try
        {
            var (vm, windowService) = CreateMainVm(rootFolder);
            windowService.Setup(w => w.ShowOpenLinqFileDialogAsync(rootFolder)).ReturnsAsync((string?)null);

            await vm.ImportLinqFileAsync();

            Assert.Empty(vm.Tabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public async Task ImportLinqFileAsync_Should_ConvertAndOpenANewSiblingTab_OnSuccess()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-linq-import-test").FullName;
        try
        {
            var linqPath = Path.Combine(rootFolder, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, TestContext.Current.CancellationToken);

            var (vm, windowService) = CreateMainVm(rootFolder);
            windowService.Setup(w => w.ShowOpenLinqFileDialogAsync(rootFolder)).ReturnsAsync(linqPath);

            await vm.ImportLinqFileAsync();

            var expectedRpadPath = Path.Combine(rootFolder, "MyQuery.rpad");
            Assert.True(File.Exists(expectedRpadPath));
            var tab = Assert.Single(vm.Tabs);
            Assert.Equal(expectedRpadPath, tab.FilePath);

            var parsed = RpadParser.TryParse(await File.ReadAllTextAsync(expectedRpadPath, TestContext.Current.CancellationToken));
            Assert.NotNull(parsed);
            Assert.Equal(RpadLanguage.CSharp, parsed!.FrontMatter.Language);
            Assert.Equal(RpadKind.Statements, parsed.FrontMatter.Kind);
            Assert.Contains("Newtonsoft.Json", parsed.FrontMatter.Packages.Keys);
            Assert.Contains("Console.WriteLine(\"hi\");", parsed.RunCode);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public async Task ImportLinqFileAsync_Should_AvoidOverwriting_WhenAnRpadWithTheSameNameAlreadyExists()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-linq-import-test").FullName;
        try
        {
            var linqPath = Path.Combine(rootFolder, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, TestContext.Current.CancellationToken);
            var existingRpadPath = Path.Combine(rootFolder, "MyQuery.rpad");
            await File.WriteAllTextAsync(existingRpadPath, "not touched", TestContext.Current.CancellationToken);

            var (vm, windowService) = CreateMainVm(rootFolder);
            windowService.Setup(w => w.ShowOpenLinqFileDialogAsync(rootFolder)).ReturnsAsync(linqPath);

            await vm.ImportLinqFileAsync();

            Assert.Equal("not touched", await File.ReadAllTextAsync(existingRpadPath, TestContext.Current.CancellationToken));
            var newRpadPath = Path.Combine(rootFolder, "MyQuery 2.rpad");
            Assert.True(File.Exists(newRpadPath));
            var tab = Assert.Single(vm.Tabs);
            Assert.Equal(newRpadPath, tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public async Task ImportLinqFileAsync_Should_ShowAnError_AndOpenNoTab_ForAnUnsupportedQueryKind()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-linq-import-test").FullName;
        try
        {
            var linqPath = Path.Combine(rootFolder, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, "<Query Kind=\"SQL\" />\n\nSELECT 1\n", TestContext.Current.CancellationToken);

            var (vm, windowService) = CreateMainVm(rootFolder);
            windowService.Setup(w => w.ShowOpenLinqFileDialogAsync(rootFolder)).ReturnsAsync(linqPath);
            windowService.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            await vm.ImportLinqFileAsync();

            windowService.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("SQL"))), Times.Once);
            Assert.Empty(vm.Tabs);
            Assert.False(File.Exists(Path.Combine(rootFolder, "MyQuery.rpad")));
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public async Task ImportLinqFileAsync_Should_ShowAnError_ForAMalformedLinqFile()
    {
        var rootFolder = Directory.CreateTempSubdirectory("runpad-linq-import-test").FullName;
        try
        {
            var linqPath = Path.Combine(rootFolder, "NotReallyLinq.linq");
            await File.WriteAllTextAsync(linqPath, "Console.WriteLine(1);\n", TestContext.Current.CancellationToken);

            var (vm, windowService) = CreateMainVm(rootFolder);
            windowService.Setup(w => w.ShowOpenLinqFileDialogAsync(rootFolder)).ReturnsAsync(linqPath);
            windowService.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            await vm.ImportLinqFileAsync();

            windowService.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Assert.Empty(vm.Tabs);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
