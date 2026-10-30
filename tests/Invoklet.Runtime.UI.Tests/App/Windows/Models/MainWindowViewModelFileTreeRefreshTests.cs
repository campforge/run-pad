using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows.Models;

// IFileSystemService.GetFilesTree() used to be synchronous, and
// MainWindowViewModel called it directly from its own constructor (before the window is ever shown)
// and on every settled FileSystemWatcher event — both ran the (potentially large/network/cloud-synced)
// recursive disk walk ON the UI thread, freezing input/rendering for its whole duration. Now
// GetFilesTreeAsync offloads the walk via Task.Run, and RefreshNodes (the fire-and-forget wrapper
// every call site but CreateFolderAsync uses) is `async void` — construction/command handlers return
// immediately regardless of how long the walk takes.
public class MainWindowViewModelFileTreeRefreshTests
{
    private static Mock<IFileSystemService> CreateFileSystemMock(string folder = "/root")
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(folder);
        return fileSystem;
    }

    private static Mock<IPreferencesService> CreatePrefsMock()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        return prefs;
    }

    [AvaloniaFact]
    public void Constructor_Should_NotBlock_WhileGetFilesTreeAsyncIsStillRunning()
    {
        var fileSystem = CreateFileSystemMock();
        // Never completed — a stand-in for a walk that's still in progress (a large/network folder).
        // If construction still synchronously waited on it (the pre-fix behavior), this test would
        // simply hang instead of failing the assertion below.
        var tcs = new TaskCompletionSource<IReadOnlyList<FileSystemItem>>();
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).Returns(tcs.Task);

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: CreatePrefsMock().Object,
            languageRegistry: new LanguageHostRegistry());

        // Construction returned (we got here at all) — Nodes being empty proves it returned WITHOUT
        // waiting for the walk to resolve, not merely that the walk happened to be fast.
        Assert.Empty(vm.FileTree.Nodes);

        vm.Dispose();
    }

    // The one exception to "fire and forget": FileTree_CreateFolder (MainWindow.axaml.cs) calls
    // FileTreeView.StartRenameForPath(newPath) immediately after CreateFolderAsync returns — which
    // needs FileTree.FindByPath(newPath) to already succeed, i.e. the refresh must have actually
    // landed by the time the awaited call completes, not merely been kicked off.
    [AvaloniaFact]
    public async Task CreateFolderAsync_Should_HaveTheNewFolderInTheTree_ByTheTimeItReturns()
    {
        var fileSystem = CreateFileSystemMock();
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        fileSystem.Setup(f => f.CreateNewFolder("/root")).Returns("/root/New Folder");

        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: CreatePrefsMock().Object,
            languageRegistry: new LanguageHostRegistry());

        // From here on, the tree the mock reports back includes the freshly created folder — mirrors
        // what a real IFileSystemService would return after CreateNewFolder actually wrote it to disk.
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FileSystemItem("New Folder", true, "/root/New Folder", [])]);

        var newPath = await vm.CreateFolderAsync(null);

        Assert.NotNull(vm.FileTree.FindByPath(newPath));

        vm.Dispose();
    }

    // Release 0.3.0: Avalonia.Headless resets the static
    // Dispatcher.UIThread between isolated tests, and the next read creates it on the reading thread.
    // The watcher debounce's late thread-pool callback used to read it there, making a pool thread
    // "the UI thread" so the next test's setup failed VerifyAccess. Simulates that reset (Avalonia's
    // private static field, restored afterwards) and waits for the callback's post without pumping
    // the UI dispatcher — pumping could recreate the static on this thread and hide the defect.
    [AvaloniaFact]
    public void LateWatcherCallback_Should_PostToTheCapturedDispatcher_WhenUIThreadWasReset()
    {
        var fileSystem = CreateFileSystemMock();
        var walks = 0;
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref walks))
            .ReturnsAsync([]);
        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: CreatePrefsMock().Object,
            languageRegistry: new LanguageHostRegistry(),
            watcherRefreshDebounce: TimeSpan.Zero); // the late pool-thread callback is the subject, not the wait
        var uiDispatcher = Dispatcher.UIThread;
        uiDispatcher.RunJobs();
        var walksBefore = Volatile.Read(ref walks);
        Assert.False(uiDispatcher.HasJobsWithPriority(DispatcherPriority.Default));

        var uiThreadField = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(uiThreadField);
        object? recreated;
        bool posted;
        try
        {
            uiThreadField.SetValue(null, null);
            Task.Run(() => fileSystem.Raise(f => f.WatcherEvent += null,
                new FileSystemEventArgs(WatcherChangeTypes.Changed, "/root", "a.inv")));

            // Either outcome ends the wait: the callback posted to the captured dispatcher, or it read
            // the static and recreated it on its own thread.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            do
            {
                recreated = uiThreadField.GetValue(null);
                posted = uiDispatcher.HasJobsWithPriority(DispatcherPriority.Default);
                if (recreated is not null || posted) break;
                Thread.Sleep(1);
            }
            while (DateTime.UtcNow < deadline);
        }
        finally
        {
            uiThreadField.SetValue(null, uiDispatcher);
        }

        Assert.Null(recreated);
        Assert.True(posted, "the watcher callback never posted its refresh");
        uiDispatcher.RunJobs();
        Assert.True(Volatile.Read(ref walks) > walksBefore, "the posted refresh never ran on the UI dispatcher");

        vm.Dispose();
    }
}
