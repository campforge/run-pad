using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.Tabs;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Reacting to a file another program changed, on fakes: no window, no editor, no files.
public class TabDiskSyncTests
{
    private sealed class FakeReader : IScriptFileReader
    {
        public Dictionary<string, ScriptFileState> Files { get; } = new();
        public HashSet<string> Unreadable { get; } = [];
        public List<string> Reads { get; } = [];
        public Action? OnRead { get; set; }
        public ScriptFileState Read(string path)
        {
            Reads.Add(path);
            OnRead?.Invoke();
            return Unreadable.Contains(path) ? throw new IOException("locked") : Files[path];
        }
    }

    private sealed class FakeHost : ITabDiskHost
    {
        public bool IsClosed { get; set; }
        public List<EditorTabItemViewModel> Tabs { get; } = [];
        IReadOnlyList<EditorTabItemViewModel> ITabDiskHost.Tabs => Tabs;
        public HashSet<EditorTabItemViewModel> WithoutContent { get; } = [];
        public List<EditorTabItemViewModel> Reloaded { get; } = [];
        public List<EditorTabItemViewModel> Reopened { get; } = [];
        public DiskReload Result { get; set; } = DiskReload.Reloaded;
        public bool HasContent(EditorTabItemViewModel tab) => !WithoutContent.Contains(tab);
        public DiskReload Reload(EditorTabItemViewModel tab)
        {
            Reloaded.Add(tab);
            return Result;
        }
        public void Reopen(EditorTabItemViewModel tab) => Reopened.Add(tab);
    }

    private static ScriptFileState State(byte b) => new([b]);

    private sealed class Harness
    {
        public Mock<IWindowService> Windows { get; } = new();
        public FakeReader Reader { get; } = new();
        public FakeHost Host { get; } = new();
        public TabDiskSync Sync { get; }

        public Harness()
        {
            Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>())).ReturnsAsync(true);
            Windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            Sync = new TabDiskSync(Windows.Object, Reader);
        }

        // A tab on a file that held state 1 when it was read and holds `onDisk` now.
        public EditorTabItemViewModel Open(string name, byte onDisk = 2, bool dirty = false)
        {
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            var path = "/scripts/" + name + ".inv";
            var tab = new EditorTabItemViewModel(path, [], prefs.Object, new Mock<IEditorTabActions>().Object,
                MdRunLanguage.CSharp, MdRunKind.Statements)
            {
                DiskState = State(1),
                IsDirty = dirty,
            };
            Reader.Files[path] = State(onDisk);
            Host.Tabs.Add(tab);
            return tab;
        }
    }

    [Fact]
    public async Task AFileThatDidNotChange_Should_LeaveTheTabAlone()
    {
        var h = new Harness();
        h.Open("a", onDisk: 1);

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
    }

    [Fact]
    public async Task AChangedFile_Should_ReloadAnUnchangedTabQuietly()
    {
        var h = new Harness();
        var tab = h.Open("a");

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Equal([tab], h.Host.Reloaded);
        h.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AChangedFile_Should_AskBeforeReloadingATabWithUnsavedChanges()
    {
        var h = new Harness();
        var tab = h.Open("a", dirty: true);

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        h.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync("a.inv"), Times.Once);
        Assert.Equal([tab], h.Host.Reloaded);
    }

    [Fact]
    public async Task ADeclinedReload_Should_BeRemembered_ForThatVersionOfTheFileOnly()
    {
        var h = new Harness();
        var tab = h.Open("a", dirty: true);
        h.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>())).ReturnsAsync(false);

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);
        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
        h.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()), Times.Once); // not asked again

        h.Reader.Files[tab.FilePath!] = State(3); // another change: asked again
        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);
        h.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ATabThatIsRunning_Should_BeLeftAlone()
    {
        var h = new Harness();
        h.Open("a").RunState = RunState.Run;

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
    }

    [Fact]
    public async Task ATabWhoseTextIsOnDisk_Should_NotBeReloaded()
    {
        var h = new Harness();
        var tab = h.Open("a");
        h.Host.WithoutContent.Add(tab);

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_Should_BeSkipped()
    {
        var h = new Harness();
        var locked = h.Open("a");
        var other = h.Open("b");
        h.Reader.Unreadable.Add(locked.FilePath!);

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Equal([other], h.Host.Reloaded);
    }

    [Fact]
    public async Task ATabClosedWhileTheQuestionWasOpen_Should_NotBeReloaded()
    {
        var h = new Harness();
        var tab = h.Open("a", dirty: true);
        h.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                h.Host.WithoutContent.Add(tab); // the tab went away behind the dialog
                return Task.FromResult(true);
            });

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
    }

    [Fact]
    public async Task AScriptInAnotherLanguage_Should_OpenInANewTab()
    {
        var h = new Harness();
        var tab = h.Open("a");
        h.Host.Result = DiskReload.OtherLanguage;

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Equal([tab], h.Host.Reopened);
    }

    [Fact]
    public async Task AFileThatVanishedWhileReloading_Should_ShowAnError()
    {
        var h = new Harness();
        h.Open("a");
        h.Host.Result = DiskReload.Unavailable;

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        h.Windows.Verify(w => w.ShowErrorAsync("Reload Failed", "Could not read 'a.inv'."), Times.Once);
    }

    [Fact]
    public async Task ACheckRequestedWhileAnotherIsOpen_Should_RunOnceMoreAfterIt()
    {
        var h = new Harness();
        var tab = h.Open("a", dirty: true);
        var gate = new TaskCompletionSource<bool>();
        var asked = 0;
        h.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() => ++asked == 1 ? gate.Task : Task.FromResult(false));

        var first = h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);
        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken); // returns at once: noted for later
        h.Reader.Files[tab.FilePath!] = State(3);
        gate.SetResult(true);
        await first;

        Assert.Equal(2, asked); // the second pass looked at the newer version
    }

    [Fact]
    public async Task ACheckCancelledWhileFilesAreBeingRead_Should_NotReadTheRest()
    {
        var h = new Harness();
        for (var i = 0; i < 20; i++) h.Open("tab" + i);
        using var cts = new CancellationTokenSource();
        h.Reader.OnRead = cts.Cancel; // the window is closed while the first file is read

        await h.Sync.CheckAsync(h.Host, cts.Token);

        Assert.Single(h.Reader.Reads);
        Assert.Empty(h.Host.Reloaded);
    }

    [Fact]
    public async Task AClosedStrip_Should_StopTheCheck()
    {
        var h = new Harness();
        h.Open("a");
        h.Host.IsClosed = true;

        await h.Sync.CheckAsync(h.Host, TestContext.Current.CancellationToken);

        Assert.Empty(h.Host.Reloaded);
    }
}
