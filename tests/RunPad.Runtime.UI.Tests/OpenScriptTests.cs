using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Moq;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.Preferences;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public sealed class OpenScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-open-test").FullName;
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IPreferencesService> _prefs = new();
    private SessionSettings _session = new();
    private readonly List<MainWindowViewModel> _models = [];

    private MainWindowViewModel Create()
    {
        _prefs.Setup(p => p.Session).Returns(() => _session);
        _prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        _prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        _prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        var fs = new Mock<IFileSystemService>();
        fs.Setup(f => f.Folder).Returns(Path.Combine(_root, "Queries"));
        fs.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var vm = new MainWindowViewModel([], fs.Object, _windows.Object, _prefs.Object, new LanguageHostRegistry());
        _models.Add(vm);
        return vm;
    }

    private string Script(string name)
    {
        var path = Path.Combine(_root, name + ".rpad");
        File.WriteAllText(path, RpadWriter.NewFile("1.Dump();", name));
        return path;
    }

    [AvaloniaFact]
    public async Task OpenPicker_Should_OpenOutsideQueries_AndCanonicalAliasesShouldActivateOneTab()
    {
        var vm = Create();
        var path = Script("outside_queries");
        _windows.Setup(w => w.ShowOpenScriptFileDialogAsync(It.IsAny<string>())).ReturnsAsync(path);
        vm.OpenTab(null);
        await vm.OpenFileCommand.Execute();
        var tab = Assert.Single(vm.Tabs);
        tab.IsDirty = true;
        await vm.OpenScriptAsync(Path.Combine(_root, ".", Path.GetFileName(path)));
        Assert.Same(tab, Assert.Single(vm.Tabs));
        Assert.True(tab.IsDirty);
        Assert.Equal(path, vm.ActiveTab!.FilePath);
        Assert.Equal([path], vm.RecentFiles);
    }

    [AvaloniaFact]
    public async Task CancelledPicker_Should_PreserveDirtyUntitledTabAndHistory()
    {
        var vm = Create();
        vm.OpenTab(null);
        var tab = vm.ActiveTab!;
        tab.IsDirty = true;
        _windows.Setup(w => w.ShowOpenScriptFileDialogAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        await vm.OpenFileCommand.Execute();
        Assert.Same(tab, Assert.Single(vm.Tabs));
        Assert.Empty(vm.RecentFiles);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task RecentFiles_Should_BeUniqueNewestFirstCappedAndPersisted()
    {
        var vm = Create();
        for (var i = 0; i < 53; i++) await vm.OpenScriptAsync(Script(i.ToString()));
        Assert.Equal(50, vm.RecentFiles.Count);
        var path = Path.Combine(_root, "20.rpad");
        await vm.OpenRecentFileCommand.Execute(path);
        Assert.Equal(path, vm.RecentFiles[0]);
        Assert.Equal(50, vm.RecentFiles.Count);
        Assert.Equal(53, vm.Tabs.Count);
        _session = JsonSerializer.Deserialize<SessionSettings>(JsonSerializer.Serialize(_session))!;
        Assert.Equal(path, Create().RecentFiles[0]);
        _prefs.Verify(p => p.Save(), Times.AtLeastOnce());
    }

    [AvaloniaFact]
    public void SessionRestoration_Should_NotReorderRecentFiles()
    {
        var first = Script("first");
        var last = Script("last");
        _session.OpenTabs = [first, last];
        _session.ActiveTabPath = first;
        _session.RecentFiles = [first, last];
        var vm = Create();
        vm.RestoreSession();
        Assert.Equal([first, last], vm.RecentFiles);
        Assert.Equal(first, vm.ActiveTab!.FilePath);
    }

    [AvaloniaFact]
    public async Task MissingRecentFile_Should_ReportErrorWithoutReplacingTabs_AndClearShouldNotDeleteFiles()
    {
        var vm = Create();
        var path = Script("exists");
        await vm.OpenScriptAsync(path);
        var tab = vm.ActiveTab;
        await vm.OpenRecentFileCommand.Execute(Path.Combine(_root, "missing.rpad"));
        _windows.Verify(w => w.ShowErrorAsync("Open Failed", It.IsAny<string>()), Times.Once);
        Assert.Same(tab, Assert.Single(vm.Tabs));
        Assert.Equal([path], vm.RecentFiles);
        await vm.ClearRecentFilesCommand.Execute();
        Assert.Empty(vm.RecentFiles);
        Assert.True(File.Exists(path));
        Assert.False(vm.HasRecentFiles);
    }

    [AvaloniaFact]
    public async Task OpeningAnotherFile_Should_KeepUnsavedUntitledTab()
    {
        var vm = Create();
        vm.OpenTab(null);
        var dirty = vm.ActiveTab!;
        dirty.IsDirty = true;
        await vm.OpenScriptAsync(Script("another"));
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Contains(dirty, vm.Tabs);
        Assert.True(dirty.IsDirty);
    }

    [AvaloniaFact]
    public async Task RepeatedOpenWhilePickerPending_Should_ShowOnlyOnePicker()
    {
        var vm = Create();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _windows.Setup(w => w.ShowOpenScriptFileDialogAsync(It.IsAny<string>())).Returns(pending.Task);
        var first = vm.OpenFileCommand.Execute().ToTask();
        var second = vm.OpenFileCommand.Execute().ToTask();
        pending.SetResult(null);
        await Task.WhenAll(first, second);
        _windows.Verify(w => w.ShowOpenScriptFileDialogAsync(It.IsAny<string>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task UnsupportedFile_Should_NotOpenOrEnterHistory()
    {
        var vm = Create();
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "not a script");
        await vm.OpenScriptAsync(path);
        Assert.Empty(vm.Tabs);
        Assert.Empty(vm.RecentFiles);
        _windows.Verify(w => w.ShowErrorAsync("Open Failed", It.IsAny<string>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task HistoryWriteFailure_Should_NotLeaveAnInvisibleTabOrBlockOpening()
    {
        var vm = Create();
        _prefs.Setup(p => p.Save()).Throws(new IOException("read-only settings"));
        var path = Script("readable");
        await vm.OpenScriptAsync(path);
        var tab = Assert.Single(vm.Tabs);
        await vm.OpenScriptAsync(path);
        Assert.Same(tab, Assert.Single(vm.Tabs));
        Assert.Same(tab, vm.ActiveTab);
    }

    [AvaloniaFact]
    public async Task RecentNotification_Should_SeeAnAlreadyRegisteredVisibleTab()
    {
        var vm = Create();
        var path = Script("visible-before-history");
        vm.RecentFilesChanged += () =>
        {
            Assert.NotNull(vm.ActiveTab);
            Assert.Contains(vm.ActiveTab, vm.Tabs);
            Assert.Equal(path, vm.ActiveTab.FilePath);
        };
        await vm.OpenScriptAsync(path);
        Assert.Single(vm.Tabs);
    }

    public void Dispose()
    {
        foreach (var vm in _models) vm.Dispose();
        Directory.Delete(_root, true);
    }
}
