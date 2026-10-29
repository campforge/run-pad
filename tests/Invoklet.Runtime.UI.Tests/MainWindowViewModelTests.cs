using Avalonia.Headless.XUnit;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

public class MainWindowViewModelTests
{
    [AvaloniaFact]
    public void ToggleSidebarCommand_Should_CollapseSidebar_When_SidebarIsExpanded()
    {
        // Arrange
        var rootFolder = Directory.CreateTempSubdirectory("Invoklet-ui-test").FullName;
        try
        {
            var fileService = new Mock<IFileSystemService>();
            fileService.Setup(f => f.Folder).Returns(rootFolder);
            fileService.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings { SidebarWidth = 300, SidebarCollapsed = false });

            var vm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileService.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            // Act
            vm.Sidebar.ToggleCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(0, vm.Sidebar.Size.Value);

            vm.Dispose();
        }
        finally
        {
            Directory.Delete(rootFolder, recursive: true);
        }
    }
}
