using Avalonia.Headless.XUnit;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

public class MainWindowViewModelTests
{
    [AvaloniaFact]
    public void ToggleSidebarCommand_Should_CollapseSidebar_When_SidebarIsExpanded()
    {
        // Arrange
        var rootFolder = Directory.CreateTempSubdirectory("dotpad-ui-test").FullName;
        try
        {
            var fileService = new Mock<IFileSystemService>();
            fileService.Setup(f => f.Folder).Returns(rootFolder);
            fileService.Setup(f => f.GetFilesTree()).Returns([]);

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings { SidebarWidth = 300, SidebarCollapsed = false });

            var vm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemSystemService: fileService.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object);

            // Act
            vm.ToggleSidebarCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(0, vm.SidebarColumnWidth.Value);

            vm.Dispose();
        }
        finally
        {
            Directory.Delete(rootFolder, recursive: true);
        }
    }
}
