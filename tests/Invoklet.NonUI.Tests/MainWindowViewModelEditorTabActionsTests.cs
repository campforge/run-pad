using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// MainWindowViewModel implements IEditorTabActions — the real behavior behind
// EditorTabItemViewModel's Close/SaveAs/Duplicate/OpenContainingFolder commands. SaveAsAsync/
// Duplicate read the write callback off tab.WriteToFile (set by EditorsTabItemControl's constructor)
// instead of taking it as a parameter, since a command invocation has no other way to supply one.
// These tests construct a tab directly through MainWindowViewModel (no EditorsTabItemControl, no
// Avalonia at all) — WriteToFile stays null the whole time, covering the narrow real window between
// a tab existing (MainWindowViewModel.AddTab) and its View finishing construction (EditorTabsControl.
// CreateControlAsync), during which the tab isn't visible/clickable yet but technically already has
// live commands.
public class MainWindowViewModelEditorTabActionsTests
{
    private static (MainWindowViewModel Vm, Mock<IWindowService> WindowService) CreateViewModel(string rootFolder)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(rootFolder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var windowService = new Mock<IWindowService>();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        return (vm, windowService);
    }

    [Fact]
    public async Task SaveAsAsync_Should_DoNothing_WhenTheTabsWriteToFileIsNotWiredYet()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-tabactions-test").FullName;
        try
        {
            var (mainVm, windowService) = CreateViewModel(rootFolder);
            mainVm.NewRpadFile();
            var tab = Assert.Single(mainVm.Tabs);
            Assert.Null(tab.WriteToFile);

            await ((IEditorTabActions)mainVm).SaveAsAsync(tab);

            windowService.Verify(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.Null(tab.FilePath);
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }

    [Fact]
    public void Duplicate_Should_DoNothing_WhenTheTabsWriteToFileIsNotWiredYet()
    {
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-tabactions-test").FullName;
        try
        {
            var (mainVm, _) = CreateViewModel(rootFolder);
            var filePath = Path.Combine(rootFolder, "demo.rpad");
            File.WriteAllText(filePath, "content");
            mainVm.OpenTab(filePath);
            var tab = Assert.Single(mainVm.Tabs);
            Assert.Null(tab.WriteToFile);

            ((IEditorTabActions)mainVm).Duplicate(tab);

            Assert.Single(mainVm.Tabs); // no second tab was opened for a copy
        }
        finally { Directory.Delete(rootFolder, recursive: true); }
    }
}
