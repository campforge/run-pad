using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

public class EditorTabsControlTests
{
    [AvaloniaFact]
    public async Task ActiveTab_Should_StaySetToTheNewTab_When_OpeningAFileClosesTheInitialBlankTab()
    {
        // Arrange: OpenTab(path) adds the new tab, activates it, then closes the now-redundant
        // initial blank tab — all synchronously — while the new tab's own control is still being
        // built off the UI thread (EditorTabsControl.CreateControlAsync). Closing the blank tab
        // removes its TabItem from the TabControl before the new tab's TabItem exists, which
        // transiently leaves the TabControl with no SelectedItem.
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-tabs-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.inv");
            File.WriteAllText(filePath, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            // Act
            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);

            // Assert
            Assert.NotNull(mainVm.ActiveTab);
            Assert.Equal(filePath, mainVm.ActiveTab.FilePath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // Bug report 2026-07-17: "if I save a file, IsDirty doesn't reset" — root cause was that there
    // was no actual Save action anywhere (no Ctrl+S, no menu item; only implicit saves on tab
    // close/Run/etc., or the always-dialog "Save As"), so pressing Ctrl+S out of habit matched no
    // gesture and silently did nothing — IsDirty correctly never cleared because nothing was ever
    // written. EditorTabsControl.SaveActive (wired to the new Ctrl+S gesture) is the fix; this
    // exercises it end-to-end against a real EditorsTabItemControl/CodeTextEditor, not just the
    // ViewModel-level IsDirty flag.
    [AvaloniaFact]
    public async Task SaveActive_Should_WriteToDiskAndClearIsDirty_ForATabWithAPath()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-save-active-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.inv");
            File.WriteAllText(filePath, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm);

            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);
            var tab = mainVm.ActiveTab!;

            // OpenTab already closed the initial blank tab (opening a real file over a single blank
            // tab), so exactly one realized control is expected here.
            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().Single();

            ctrl.State.Editor.Text = "Console.WriteLine(2);";
            Assert.True(tab.IsDirty);

            editorTabs.SaveActive();

            Assert.False(tab.IsDirty);
            var written = MdRunParser.TryParse(File.ReadAllText(filePath));
            Assert.NotNull(written);
            Assert.Contains("Console.WriteLine(2);", written!.RunCode);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SaveActive_Should_FallBackToSaveAsDialog_ForAnUntitledTab()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-save-active-untitled-test").FullName;
        try
        {
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var chosenPath = Path.Combine(tmpDir, "NewScript.inv");
            var windowService = new Mock<IWindowService>();
            windowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), tmpDir))
                .ReturnsAsync(chosenPath);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: windowService.Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial untitled tab — no dialog wait needed, no disk file to parse
            for (var i = 0; i < 100 && editorTabs.GetVisualDescendants().OfType<TabItem>()
                     .Select(t => t.Content).OfType<EditorsTabItemControl>().SingleOrDefault() is null; i++)
                await Task.Delay(20);

            var tab = Assert.Single(mainVm.Tabs);
            var ctrl = editorTabs.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>().Single();
            ctrl.State.Editor.Text = "Console.WriteLine(1);";
            Assert.True(tab.IsDirty);

            editorTabs.SaveActive();
            await TestWait.WhileAsync(() => !File.Exists(chosenPath));

            Assert.True(File.Exists(chosenPath));
            Assert.Equal(chosenPath, tab.FilePath);
            Assert.False(tab.IsDirty);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // EditorTabItemControl now pushes the tab's front-matter `kind:` into its
    // IntelliSense instance (mirroring the existing RefreshIntelliSensePackages/RefreshIntelliSenseUsings
    // pattern) so CSharpIntelliSenseService's live-analysis document can mirror the same kind-specific
    // transform the compiler applies (e.g. Program's implicit `Main();` call) — without this wiring,
    // the service-level fix (CSharpIntelliSenseService.UpdateKind) is never actually reached from a
    // real opened file.
    [AvaloniaFact]
    public async Task OpenTab_Should_PushTheFileKindIntoIntelliSense_ForAProgramKindFile()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-tabs-kind-test").FullName;
        try
        {
            var filePath = Path.Combine(tmpDir, "demo.inv");
            File.WriteAllText(filePath, MdRunWriter.NewFile("void Main()\n{\n    \n}\n", "Demo", kind: MdRunKind.Program));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            mainVm.OpenTab(filePath);
            await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);

            intelliSense.Verify(x => x.UpdateKind(MdRunKind.Program), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // CreateControlAsync used to always MdRunFilePeek.PeekLanguage
    // (vm.FilePath) to decide whether to build a C# IntelliSense instance — harmless while every new
    // file was written to disk (with correct front matter) before its tab ever opened, since
    // PeekLanguage would then read the real language back off disk. Now that file creation is
    // deferred (NewMdRunFile no longer touches disk), PeekLanguage(null) always answers C# regardless
    // of what the user actually picked, which would wire a live C# IntelliSense instance up against
    // VB source text for a brand-new VB tab until its first save.
    [AvaloniaFact]
    public async Task NewMdRunFile_ForVb_Should_NotBuildACSharpIntelliSenseInstance()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-tabs-vb-newfile-test").FullName;
        try
        {
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();
            var intelliSenseFactoryCalls = 0;

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => { intelliSenseFactoryCalls++; return intelliSense.Object; }), prefs.Object,
                buildCache.Object);

            var host = new Window { Content = editorTabs };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial (C#) blank tab — 1 factory call expected

            // Wait for the initial blank (C#) tab's own factory call — it runs its Task.Run off the
            // UI thread (see CreateControlAsync), so it isn't guaranteed to have landed the instant
            // Attach() returns above.
            await TestWait.WhileAsync(() => intelliSenseFactoryCalls < 1);
            Assert.Equal(1, intelliSenseFactoryCalls);

            mainVm.NewMdRunFile(MdRunLanguage.Vb);
            await TestWait.WhileAsync(() => mainVm.Tabs.Count < 2);

            // The VB tab's own CreateControlAsync never hits a real await (PeekLanguage-equivalent
            // resolves to Vb synchronously, see the comment above), so there's no async gap to wait
            // out here — but poll a little longer anyway rather than assert immediately, in case that
            // assumption ever stops holding.
            await Task.Delay(50);
            Assert.Equal(1, intelliSenseFactoryCalls);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // Reported by user 2026-07-16: opening/creating enough scripts that the tab headers no longer
    // fit used to make them overlap (WrapPanel capped to a single row's height via MaxHeight="30",
    // see EditorTabsControl.axaml) instead of being reachable at all. FileTabControl's ItemsPanel is
    // now a non-wrapping horizontal StackPanel wrapped in a horizontally-scrolling ScrollViewer — this
    // exercises real (headless, but genuine) layout to prove the fix: enough tabs to overflow a
    // narrow window still land on a single row (no second-row overlap) and the surrounding
    // ScrollViewer's content is actually wider than its viewport (so the overflow is reachable by
    // scrolling, not just silently clipped).
    [AvaloniaFact]
    public async Task FileTabControl_Should_KeepTabsOnOneRowAndMakeThemScrollable_WhenTooManyToFit()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-tabs-overflow-test").FullName;
        try
        {
            const int tabCount = 25;
            var paths = new List<string>();
            for (var i = 0; i < tabCount; i++)
            {
                var path = Path.Combine(tmpDir, $"Query {i}.inv");
                File.WriteAllText(path, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", $"Query {i}"));
                paths.Add(path);
            }

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(tmpDir);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();
            var intelliSense = new Mock<IIntelliSenseService>();

            var mainVm = new MainWindowViewModel(
                sdkProviders: [],
                fileSystemService: fileSystem.Object,
                windowService: new Mock<IWindowService>().Object,
                prefs: prefs.Object,
                languageRegistry: new LanguageHostRegistry());

            var editorTabs = new EditorTabsControl(
                theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
                buildCache.Object);

            // Narrow enough that 25 tab headers can't possibly fit on one screen's width unscrolled —
            // the point is to force the overflow condition deterministically, not to match a real
            // window size.
            var host = new Window { Content = editorTabs, Width = 600, Height = 400 };
            host.Show();
            editorTabs.Attach(mainVm); // opens the initial blank tab

            foreach (var path in paths)
                mainVm.OpenTab(path);

            var fileTabControl = editorTabs.GetVisualDescendants().OfType<TabControl>()
                .First(t => t.Name == "FileTabControl");

            // Not pinned to an exact count: whether the initial blank tab survives opening the first
            // real file is an unrelated timing detail (see OnFileTabSelectionChanged's own comments)
            // — all that matters here is that every one of the tabs actually opened.
            await TestWait.WhileAsync(() => fileTabControl.Items.Count < tabCount);

            // Item count alone doesn't mean layout has actually run for the newly realized
            // containers — flush the dispatcher at Background priority (below Layout) so any queued
            // measure/arrange work finishes before Bounds is read below.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var tabItems = fileTabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            Assert.True(tabItems.Count >= tabCount,
                $"Expected at least {tabCount} realized tab headers, found {tabItems.Count}.");

            // Guards the two checks below against passing vacuously against never-laid-out (all-zero)
            // Bounds — if this ever fails, it means the dispatcher flush above stopped being enough
            // to wait for a real layout pass, not that the fix itself regressed.
            Assert.All(tabItems, t => Assert.True(t.Bounds.Width > 0));

            // All tab headers share the same Y — none of them got pushed onto a clipped/overlapping
            // second row.
            var distinctTops = tabItems.Select(t => t.Bounds.Y).Distinct().ToList();
            Assert.Single(distinctTops);

            // The scroll viewer wrapping the tab strip actually has overflow content to scroll to —
            // proves the extra tabs are reachable, not just clipped off past the visible width.
            var scrollViewer = fileTabControl.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.Name == "PART_TabStripScrollViewer");
            Assert.True(scrollViewer.Extent.Width > scrollViewer.Viewport.Width,
                $"Extent={scrollViewer.Extent}, Viewport={scrollViewer.Viewport} — the tab strip should overflow its own viewport for this many tabs in a {host.Width}px-wide window.");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
