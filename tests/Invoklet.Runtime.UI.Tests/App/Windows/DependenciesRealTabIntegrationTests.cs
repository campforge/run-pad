using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReactiveUI;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows;

// Reproduces "packages added via Dependencies aren't there for an already-open, already-saved
// .inv file" end-to-end: real EditorTabsControl/MainWindowViewModel tab loading (the same async
// path CreateControlAsync uses), real ILocalPackageCacheService, real DependenciesWindow —
// IPackageSearchService itself is mocked (no real network dependency in CI), only the DI-only
// services with no bearing on this flow are mocked otherwise.
[Trait("Category", "Integration")]
public class DependenciesRealTabIntegrationTests
{
    [AvaloniaFact]
    public async Task DependenciesWindow_Should_ShowExistingPackagesAndSearchResults_When_OpenedForAnAlreadyLoadedMdRunFile()
    {
        // Arrange
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-deps-it").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.inv");
            var doc = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"))!;
            doc.FrontMatter.Packages["Existing.Package"] = new Package("1.2.3");
            File.WriteAllText(filePath, MdRunWriter.Write(doc));

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
            var intelliSense = new Mock<IIntelliSenseService>();

            var search = new Mock<IPackageSearchService>();
            search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageSearchPage([new PackageSearchResult("Newtonsoft.Json", "13.0.3", "desc", 100)], HasMore: false));
            var sources = new Mock<INuGetSourceStore>();
            sources.Setup(s => s.Load()).Returns([new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json")]);
            search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["13.0.3"]);

            var services = new ServiceCollection();
            services.AddSingleton(search.Object);
            services.AddSingleton(sources.Object);

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
                theme, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            DependenciesWindow? depsWindow = null;
            try
            {
                host.Show();
                editorTabs.Attach(mainVm); // opens the initial blank tab

                // Act: open the already-saved .inv file, same as double-clicking it in the file tree,
                // and wait for the async tab load (EditorTabsControl.CreateControlAsync) to finish.
                mainVm.OpenTab(filePath);
                await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);

                Assert.NotNull(mainVm.ActiveTab?.FrontMatter);

                // Same subscription WindowService.ShowDependenciesAsync sets up, minus the modal
                // ShowDialog plumbing (which needs a real classic-desktop ApplicationLifetime that
                // Avalonia.Headless.XUnit doesn't provide) — Show() is enough to inspect real bindings.
                var depsVm = sp.GetRequiredService<DependenciesWindowViewModel>();
                depsWindow = new DependenciesWindow(depsVm);
                using var sub = mainVm.ActiveTab!.WhenAnyValue(x => x.FrontMatter).Subscribe(depsWindow.SetFrontMatter);
                depsWindow.Show();

                Assert.Contains(depsVm.LocalPackages, p => p.Id == "Existing.Package" && p.IsInScript);

                var searchBox = depsWindow.FindControl<TextBox>("SearchBox")!;
                searchBox.Focus();
                depsWindow.KeyTextInput("Newtonsoft.Json");
                // The search itself is mocked (returns instantly) — only the ViewModel's own 350ms
                // debounce stands between typing and results, so poll instead of a flat 3s sleep. The
                // 3000ms ceiling is kept only as a generous upper bound for slow CI machines.
                await TestWait.WhileAsync(() => depsVm.OnlinePackages.Count == 0);

                // Assert
                Assert.True(depsVm.HasFrontMatter);
                Assert.True(depsVm.OnlinePackages.Count > 0, "Expected the mocked search result for 'Newtonsoft.Json'.");
            }
            finally
            {
                depsWindow?.Close();
                host.Close();
                editorTabs.Dispose();
                mainVm.Dispose();
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
