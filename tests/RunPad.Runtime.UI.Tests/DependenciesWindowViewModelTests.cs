using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services.Preferences;
using RunPad.UserControls.Models;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class DependenciesWindowViewModelTests
{
    [AvaloniaFact]
    public async Task AddCommand_Should_BeEnabled_When_ActiveTabHasFrontMatter()
    {
        // Arrange
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"));
        Assert.NotNull(rpad);

        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PackageSearchResult>)
                [new PackageSearchResult("Newtonsoft.Json", "13.0.3", "desc", 100)]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);

        var vm = new DependenciesWindowViewModel(search.Object, cache.Object);
        var window = new DependenciesWindow(vm);
        window.SetFrontMatter(rpad!.FrontMatter);
        window.Show();

        var searchBox = window.GetVisualDescendants().OfType<TextBox>().First();
        searchBox.Focus();
        window.KeyTextInput("Newtonsoft.Json");

        // Poll instead of a fixed delay: debounce (350ms) inside DependenciesWindowViewModel.TriggerSearch
        // plus rendering can comfortably exceed a fixed wait under slower/loaded CI (esp. Windows).
        Button? addButton = null;
        for (var i = 0; i < 250 && addButton is null; i++)
        {
            await Task.Delay(20);
            addButton = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Content as string == "+");
        }
        Assert.NotNull(addButton);
        var clickPoint = addButton!.TranslatePoint(
            new Point(addButton.Bounds.Width / 2, addButton.Bounds.Height / 2), window)!.Value;

        // Act
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);

        // Assert
        Assert.Contains("Newtonsoft.Json", rpad.FrontMatter.Packages.Keys);
    }

    [AvaloniaFact]
    public void HasFrontMatter_Should_BecomeTrue_When_ActiveTabFinishesLoadingAfterDialogOpens()
    {
        // Arrange: EditorTabsControl loads the Roslyn workspace/BCL metadata for a newly-opened tab
        // off the UI thread (EditorTabsControl.CreateControlAsync), so EditorTabItemViewModel.FrontMatter
        // can still be null at the instant the Dependencies window is opened for that (already-active)
        // tab. WindowService.ShowDependenciesAsync subscribes to the tab's FrontMatter stream instead of
        // taking a one-time snapshot specifically so this doesn't leave the dialog stuck disabled.
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        var tab = new EditorTabItemViewModel(
            filePath: "demo.rpad", sdks: [], prefs: prefs.Object, actions: new Mock<IEditorTabActions>().Object);

        var vm = new DependenciesWindowViewModel(
            new Mock<IPackageSearchService>().Object,
            new Mock<ILocalPackageCacheService>().Object);
        var window = new DependenciesWindow(vm);

        // Simulates WindowService.ShowDependenciesAsync's subscription (rather than a one-time snapshot).
        using var subscription = tab.WhenAnyValue(x => x.FrontMatter).Subscribe(window.SetFrontMatter);

        // Dialog opens before the tab's async load has set FrontMatter.
        Assert.False(vm.HasFrontMatter);

        // Act: the async tab load completes and assigns the real front matter moments later.
        var rpad = RpadParser.TryParse(RpadWriter.NewFile("Demo"));
        tab.FrontMatter = rpad!.FrontMatter;

        // Assert
        Assert.True(vm.HasFrontMatter);
    }
}
