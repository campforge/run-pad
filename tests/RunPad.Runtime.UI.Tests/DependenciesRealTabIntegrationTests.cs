using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Reproduces "packages added via Dependencies aren't there for an already-open, already-saved
// .rpad file" end-to-end: real EditorTabsControl/MainWindowViewModel tab loading (the same async
// path CreateControlAsync uses), real ILocalPackageCacheService, real DependenciesWindow —
// IPackageSearchService itself is mocked (no real network dependency in CI), only the DI-only
// services with no bearing on this flow are mocked otherwise.
public class DependenciesRealTabIntegrationTests
{
    [AvaloniaFact]
    public async Task DependenciesWindow_Should_ShowExistingPackagesAndSearchResults_When_OpenedForAnAlreadyLoadedRpadFile()
    {
        // Arrange
        var tmpDir = Directory.CreateTempSubdirectory("runpad-deps-it").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            var doc = RpadParser.TryParse(RpadWriter.NewFile("Demo"))!;
            doc.FrontMatter.Packages["Existing.Package"] = new Package("1.2.3");
            File.WriteAllText(filePath, RpadWriter.Write(doc));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
            prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

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

            var windowService = new WindowService(
                () => sp.GetRequiredService<DependenciesWindow>(),
                () => sp.GetRequiredService<PreferencesWindow>(),
                theme);
            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: windowService,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
                () => intelliSense.Object, referenceService, buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            // Act: open the already-saved .rpad file, same as double-clicking it in the file tree,
            // and wait for the async tab load (EditorTabsControl.CreateControlAsync) to finish.
            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);

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
            // The search itself is mocked (returns instantly) — only the ViewModel's own 350ms
            // debounce stands between typing and results, so poll instead of a flat 3s sleep. The
            // 3000ms ceiling is kept only as a generous upper bound for slow CI machines.
            await TestWait.WhileAsync(() => depsVm.Results.Count == 0);

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
