using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows;
using DotPad.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReactiveUI;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// Reproduces "packages added via Dependencies aren't there for an already-open, already-saved
// .rpad file" end-to-end: real EditorTabsControl/MainWindowViewModel tab loading (the same async
// path CreateControlAsync uses), real NuGetPackageSearchService/LocalPackageCacheService, real
// DependenciesWindow — only the DI-only services with no bearing on this flow are mocked.
public class DependenciesRealTabIntegrationTests
{
    [AvaloniaFact]
    public async Task DependenciesWindow_Should_ShowExistingPackagesAndSearchResults_When_OpenedForAnAlreadyLoadedRpadFile()
    {
        // Arrange
        var tmpDir = Directory.CreateTempSubdirectory("dotpad-deps-it").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            var doc = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
            doc.FrontMatter.Packages["Existing.Package"] = new Package("1.2.3");
            File.WriteAllText(filePath, RpadWriter.Write(doc));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
            prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTree()).Returns([]);

            var theme = new ThemeService(Application.Current!, prefs.Object);
            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            buildCache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Path.Combine(tmpDir, "out"));
            var referenceService = new CSharpReferenceService();
            var intelliSense = new Mock<IIntelliSenseService>();

            var search = new Mock<IPackageSearchService>();

            search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))

                .ReturnsAsync((IReadOnlyList<PackageSearchResult>)

                    [new PackageSearchResult("Newtonsoft.Json", "13.0.3", "desc", 100)]);



            var services = new ServiceCollection();
            services.AddSingleton(search.Object);

            services.AddSingleton<ILocalPackageCacheService, LocalPackageCacheService>();
            services.AddTransient<DependenciesWindowViewModel>();
            services.AddTransient<DependenciesWindow>();
            var sp = services.BuildServiceProvider();

            var windowService = new WindowService(sp);
            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemSystemService: fileSystem.Object,
                windowService: windowService,
                prefs: prefs.Object);

            var editorTabs = new EditorTabsControl(
                theme, registry.Object, prefs.Object, () => intelliSense.Object, referenceService, buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            // Act: open the already-saved .rpad file, same as double-clicking it in the file tree,
            // and wait for the async tab load (EditorTabsControl.CreateControlAsync) to finish.
            mainVm.OpenTab(filePath);
            for (var i = 0; i < 100 && mainVm.ActiveTab?.FrontMatter is null; i++)
                await Task.Delay(20);

            Assert.NotNull(mainVm.ActiveTab?.FrontMatter);

            // Same subscription WindowService.ShowDependenciesAsync sets up, minus the modal
            // ShowDialog plumbing (which needs a real classic-desktop ApplicationLifetime that
            // Avalonia.Headless.XUnit doesn't provide) — Show() is enough to inspect real bindings.
            var depsVm = sp.GetRequiredService<DependenciesWindowViewModel>();
            var depsWindow = new DependenciesWindow(depsVm);
            using var sub = mainVm.ActiveTab!.WhenAnyValue(x => x.FrontMatter).Subscribe(depsWindow.SetFrontMatter);
            depsWindow.Show();

            var searchBox = depsWindow.GetVisualDescendants().OfType<TextBox>().First();
            searchBox.Focus();
            depsWindow.KeyTextInput("Newtonsoft.Json");
            await Task.Delay(3000); // debounce (350ms) + real NuGet search round-trip

            // Assert
            Assert.True(depsVm.HasFrontMatter);
            Assert.Contains(depsVm.ScriptPackages, p => p.Id == "Existing.Package");
            Assert.True(depsVm.Results.Count > 0, "Expected at least one real NuGet search result for 'Newtonsoft.Json'.");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
