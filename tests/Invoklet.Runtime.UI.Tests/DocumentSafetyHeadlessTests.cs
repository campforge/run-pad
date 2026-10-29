using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// End-to-end against a real MainWindow + EditorTabsControl +
// EditorsTabItemControl. The tab's writer is replaced with one that throws IOException, so a write
// failure is deterministic on every OS; the assertions check the SAME control instance and its live
// editor text survive a cancelled close/quit, not merely that some tab for the path still exists.
public class DocumentSafetyHeadlessTests
{
    private sealed record Harness(
        MainWindow Window, MainWindowViewModel MainVm, Mock<IWindowService> WindowService, string Root);

    private static Harness CreateHarness()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-docsafety-ui-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var windowService = new Mock<IWindowService>();
        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        var intelliSense = new Mock<IIntelliSenseService>();
        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
            new Mock<IBuildCacheService>().Object);

        var window = new MainWindow(prefs.Object, editorTabs, mainVm);
        window.Show();
        return new Harness(window, mainVm, windowService, root);
    }

    private static async Task<(EditorTabItemViewModel Tab, EditorsTabItemControl Control)> OpenNamedAsync(
        Harness h, string name)
    {
        var path = Path.Combine(h.Root, name);
        File.WriteAllText(path, MdRunWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", Path.GetFileNameWithoutExtension(name)));
        h.MainVm.OpenTab(path);
        var tab = h.MainVm.Tabs.Single(t => t.FilePath == path);
        var ctrl = await FindControlAsync(h.Window, tab);
        return (tab, ctrl);
    }

    private static async Task<EditorsTabItemControl> FindControlAsync(MainWindow window, EditorTabItemViewModel tab)
    {
        for (var i = 0; i < 100; i++)
        {
            var ctrl = window.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .FirstOrDefault(c => ReferenceEquals(c.DataContext, tab));
            if (ctrl is not null && tab.FrontMatter is not null) return ctrl;
            await Task.Delay(20);
        }
        throw new TimeoutException("The tab's control was not realized in time.");
    }

    private static void ThrowDiskFull(string _) => throw new IOException("disk full");

    // Runs after the assertions: the tabs' dirty flags are dropped first so closing the window
    // neither asks nor writes anything (no production Save/Cancel path), then the window — whose
    // Dispose releases the EditorTabsControl and its tab controls — and the view model are disposed.
    private static void Cleanup(Harness h)
    {
        try
        {
            foreach (var tab in h.MainVm.Tabs)
                tab.IsDirty = false;
            h.Window.Close();
            Assert.False(h.Window.IsVisible);
        }
        finally
        {
            h.Window.Dispose();
            h.MainVm.Dispose();
        }

        foreach (var file in Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(h.Root, recursive: true);
    }

    [AvaloniaFact]
    public async Task ClosingADirtyNamedTab_WhoseWriteThrows_AndCancelling_Should_KeepTheSameControlAndText()
    {
        var h = CreateHarness();
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "a.rpad");
            ctrl.State.Editor.Text = "Console.WriteLine(\"unsaved\");";
            tab.WriteToFile = ThrowDiskFull;
            h.WindowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            Assert.Contains(tab, h.MainVm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.Same(ctrl, await FindControlAsync(h.Window, tab));
            Assert.Equal("Console.WriteLine(\"unsaved\");", ctrl.State.Editor.Text);
            h.WindowService.Verify(w => w.ShowErrorAsync("Save Failed", It.Is<string>(m => m.Contains("disk full"))), Times.Once);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task QuittingWithAnInactiveDirtyNamedTab_WhoseWriteThrows_AndCancelling_Should_KeepTheWindowControlAndText()
    {
        var h = CreateHarness();
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "a.rpad");
            ctrl.State.Editor.Text = "Console.WriteLine(\"unsaved\");";
            tab.WriteToFile = ThrowDiskFull;
            await OpenNamedAsync(h, "b.rpad"); // a.rpad becomes inactive
            Assert.NotSame(tab, h.MainVm.ActiveTab);
            h.WindowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            var closed = false;
            h.Window.Closed += (_, _) => closed = true;
            h.Window.Close();
            await Task.Delay(50);

            Assert.False(closed);
            Assert.Contains(tab, h.MainVm.Tabs);
            Assert.True(tab.IsDirty);
            Assert.Same(ctrl, await FindControlAsync(h.Window, tab));
            Assert.Equal("Console.WriteLine(\"unsaved\");", ctrl.State.Editor.Text);
        }
        finally { Cleanup(h); }
    }

    // Kind/usings/packages edits were written without ever flagging the tab dirty.
    // A failed write left a "clean" tab that the next close dropped along with the edit.
    [AvaloniaFact]
    public async Task ChangingKind_WhenTheWriteFails_Should_LeaveTheTabDirty_SoCloseStillAsks()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var h = CreateHarness();
        var path = Path.Combine(h.Root, "a.rpad");
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "a.rpad");
            Assert.False(tab.IsDirty);
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            h.WindowService.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()))
                .ReturnsAsync(UnsavedChangesResult.Cancel);

            tab.SelectedKind = nameof(MdRunKind.Program);

            Assert.True(tab.IsDirty);
            Assert.Contains("Save failed", ctrl.State.ExecStatusText);

            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);

            Assert.Contains(tab, h.MainVm.Tabs);
            h.WindowService.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Once);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Cleanup(h);
        }
    }

    [AvaloniaFact]
    public async Task ChangingKind_WhenTheWriteSucceeds_Should_PersistIt_AndLeaveTheTabClean()
    {
        var h = CreateHarness();
        try
        {
            var (tab, _) = await OpenNamedAsync(h, "a.rpad");

            tab.SelectedKind = nameof(MdRunKind.Program);

            Assert.False(tab.IsDirty);
            var saved = MdRunParser.TryParse(File.ReadAllText(tab.FilePath!));
            Assert.Equal(MdRunKind.Program, saved!.FrontMatter.Kind);
        }
        finally { Cleanup(h); }
    }

    // Dependencies window edits the front matter in place; only a real change counts — merely
    // opening and closing it must not make a pristine untitled tab ask on close.
    [AvaloniaFact]
    public async Task DependenciesClosed_OnAnUntitledTab_Should_MarkDirtyOnlyForARealChange()
    {
        var h = CreateHarness();
        try
        {
            var tab = Assert.Single(h.MainVm.Tabs);
            Assert.Null(tab.FilePath);
            var ctrl = await FindControlAsync(h.Window, tab);

            ctrl.OnDependenciesEdited();
            Assert.False(tab.IsDirty);

            tab.FrontMatter!.Packages["Newtonsoft.Json"] = new Package("13.0.3");
            ctrl.OnDependenciesEdited();
            Assert.True(tab.IsDirty);
        }
        finally { Cleanup(h); }
    }

    // Dependencies dialog driven through MainWindowViewModel.OpenDependenciesCommand: the mocked
    // window service hosts a real DependenciesWindowViewModel on the active tab's FrontMatter stream,
    // edits the draft, then either applies it (OK) or drops it (Cancel/Escape/close).
    private static void SetupDependenciesDialog(Harness h, bool apply)
        => h.WindowService.Setup(w => w.ShowDependenciesAsync(It.IsAny<IObservable<MdRunFrontMatter?>?>(), It.IsAny<IObservable<string?>?>()))
            .Returns<IObservable<MdRunFrontMatter?>?, IObservable<string?>?>((stream, _) =>
            {
                var cache = new Mock<ILocalPackageCacheService>();
                cache.Setup(c => c.GetCachedPackages()).Returns([]);
                var vm = new DependenciesWindowViewModel(new Mock<IPackageSearchService>().Object, cache.Object,
                    new Mock<INuGetSourceStore>().Object);
                using (stream!.Subscribe(vm.SetFrontMatter))
                {
                    vm.NamespacesText += "\nSystem.Linq";
                    Assert.True(vm.HasChanges);
                    if (apply) vm.ApplyChanges();
                }
                vm.Dispose();
                return Task.CompletedTask;
            });

    [AvaloniaFact]
    public async Task CancellingDependencies_OnADirtyNamedTab_Should_NotWriteTheUnsavedText()
    {
        var h = CreateHarness();
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "a.rpad");
            var onDisk = File.ReadAllText(tab.FilePath!);
            ctrl.State.Editor.Text = "Console.WriteLine(\"unsaved\");";
            Assert.True(tab.IsDirty);
            SetupDependenciesDialog(h, apply: false);

            await h.MainVm.OpenDependenciesCommand.Execute();

            Assert.Equal(onDisk, File.ReadAllText(tab.FilePath!));
            Assert.True(tab.IsDirty);
            Assert.DoesNotContain("System.Linq", tab.FrontMatter!.Namespaces ?? []);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ApplyingDependencies_Should_SaveTheChange_AndLeaveTheTabClean()
    {
        var h = CreateHarness();
        try
        {
            var (tab, _) = await OpenNamedAsync(h, "a.rpad");
            SetupDependenciesDialog(h, apply: true);

            await h.MainVm.OpenDependenciesCommand.Execute();

            Assert.False(tab.IsDirty);
            var saved = MdRunParser.TryParse(File.ReadAllText(tab.FilePath!));
            Assert.Contains("System.Linq", saved!.FrontMatter.Namespaces!);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task ApplyingDependencies_WhenTheWriteFails_Should_LeaveTheTabDirty()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var h = CreateHarness();
        var path = Path.Combine(h.Root, "a.rpad");
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "a.rpad");
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            SetupDependenciesDialog(h, apply: true);

            await h.MainVm.OpenDependenciesCommand.Execute();

            Assert.True(tab.IsDirty);
            Assert.Contains("Save failed", ctrl.State.ExecStatusText);
            Assert.Contains("System.Linq", tab.FrontMatter!.Namespaces!);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Cleanup(h);
        }
    }
    [AvaloniaFact]
    public async Task FailedSaveAsAndRetry_Should_PreserveLiveTextAndOldFile()
    {
        var h = CreateHarness();
        try
        {
            var (tab, ctrl) = await OpenNamedAsync(h, "original.rpad");
            var oldPath = tab.FilePath!;
            var oldBytes = File.ReadAllBytes(oldPath);
            const string edited = "Console.WriteLine(\"Привет 🌍\");";
            ctrl.State.Editor.Text = edited;
            var destination = Path.Combine(h.Root, "retry.rpad");
            h.WindowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), h.Root))
                .ReturnsAsync(destination);

            await h.MainVm.SaveTabAsAsync(tab, ThrowDiskFull);
            Assert.True(tab.IsDirty);
            Assert.Equal(oldPath, tab.FilePath);
            Assert.Equal(edited, ctrl.State.Editor.Text);
            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
            Assert.Contains(tab, h.MainVm.Tabs);

            await h.MainVm.SaveTabAsAsync(tab, ctrl.WriteFile);
            Assert.False(tab.IsDirty);
            Assert.Equal(destination, tab.FilePath);
            Assert.Equal(edited + "\n", MdRunParser.TryParse(File.ReadAllText(destination))!.RunCode);
            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        }
        finally { Cleanup(h); }
    }

}
