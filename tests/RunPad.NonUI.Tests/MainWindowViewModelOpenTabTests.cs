using Moq;
using RunPad.Contracts;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// _openTabs used StringComparer.OrdinalIgnoreCase unconditionally, which is correct
// for Windows'/macOS' default case-insensitive filesystems but wrong on Linux's case-sensitive one —
// two genuinely different files differing only by case (Query1.rpad/query1.rpad) would collapse
// into a single dictionary entry there. Fixed with an OS-aware comparer.
public class MainWindowViewModelOpenTabTests
{
    [Fact]
    public void OpenTab_TreatsCaseDifferingPaths_AsDistinctTabs_OnLinux()
    {
        // Linux-only: _openTabs uses a case-sensitive comparer there (Ordinal), so two paths
        // differing only by case really are two tabs. On Windows/macOS the comparer is
        // OrdinalIgnoreCase by design (case-insensitive filesystems), so this assertion wouldn't
        // hold — skip instead of failing CI on those runners. Same pattern as
        // SaveIfNeededFailureTests' OS guard.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns("/tmp");
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

        vm.OpenTab("/tmp/Query1.rpad");
        vm.OpenTab("/tmp/query1.rpad");

        Assert.Equal(2, vm.Tabs.Count(t => t.FilePath is not null));
    }

    [Fact]
    public void OpenTab_ReactivatesExistingTab_ForTheSameCasedPath()
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns("/tmp");
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

        vm.OpenTab("/tmp/Query1.rpad");
        var firstTab = vm.ActiveTab;
        vm.OpenTab("/tmp/Query1.rpad");

        Assert.Same(firstTab, vm.ActiveTab);
        Assert.Single(vm.Tabs, t => t.FilePath is not null);
    }
}
