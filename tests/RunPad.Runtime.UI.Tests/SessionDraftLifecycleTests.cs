using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.Services.Recovery;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public sealed class SessionDraftLifecycleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-session-drafts").FullName;
    private string RecoveryRoot => Path.Combine(_root, "recovery");

    private sealed class Session : IDisposable
    {
        public Mock<IWindowService> Windows { get; } = new();
        public MainWindowViewModel Vm { get; }
        public EditorTabsControl Tabs { get; }
        public MainWindow Host { get; }
        public IRecoveryStore Store { get; }

        public Session(string root, IRecoveryStore store, bool enabled = true)
        {
            Store = store;
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
            prefs.Setup(p => p.Appearance).Returns(new AppearanceSettings { RestoreUnsavedTabs = enabled });
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            var files = new Mock<IFileSystemService>();
            files.Setup(f => f.Folder).Returns(root);
            files.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
            var registry = new Mock<ILanguageHostRegistry>();
            Vm = new([], files.Object, Windows.Object, prefs.Object, new LanguageHostRegistry(), recovery: store);
            Tabs = new(theme.Object, Windows.Object, registry.Object, prefs.Object, new Mock<IBuildCacheService>().Object);
            Host = new(prefs.Object, Tabs, Vm);
            Host.Show();
        }

        public async Task Ready()
        {
            await Vm.RestoreSessionAsync();
            await Task.WhenAll(Vm.Tabs.Select(t => t.RecoveryReady.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public EditorsTabItemControl Control(EditorTabItemViewModel tab) => Tabs.ControlForTests(tab)!;
        public async Task Close()
        {
            Host.Close();
            await TestWait.WhileAsync(() => Host.IsVisible);
        }

        public void Dispose()
        {
            Host.Dispose();
            Vm.Dispose();
            Store.Dispose();
            if (Host.IsVisible) Host.Close();
        }
    }

    [AvaloniaFact]
    public async Task NormalExitAndRestart_Should_RestoreOrderActiveTextAndMetadata_WithoutWritingOriginals()
    {
        var path = Path.Combine(_root, "named.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("saved", "Named"));
        var original = File.ReadAllBytes(path);
        Guid active;
        using (var first = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await first.Ready();
            var blank = Assert.Single(first.Vm.Tabs);
            first.Control(blank).State.Editor.Text = "untitled\r\n${0}";
            var fm = first.Control(blank).State.RpadDocument!.FrontMatter;
            fm.Args = ["", "a b", "Ж"];
            fm.References = ["relative/a.dll", "$(RuntimeDirectory)/System.dll"];
            fm.Namespaces = ["System.Text"];
            fm.Packages["Example"] = new Package("1.2.3");
            fm.TargetSdk = new("net", "8.0");
            first.Vm.OpenTab(path);
            await first.Ready();
            first.Control(first.Vm.Tabs[1]).State.Editor.Text = "dirty named";
            first.Vm.OpenTab(null);
            await first.Ready();
            active = blank.RecoveryId;
            first.Vm.ActiveTab = blank;
            await first.Close();
            first.Windows.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        Assert.Equal(3, next.Vm.Tabs.Count);
        Assert.Equal(new string?[] { null, path, null }, next.Vm.Tabs.Select(t => t.FilePath));
        Assert.Equal(active, next.Vm.ActiveTab!.RecoveryId);
        var restored = next.Control(next.Vm.Tabs[0]);
        Assert.Equal("untitled\r\n${0}", restored.State.Editor.Text);
        Assert.Equal(new[] { "", "a b", "Ж" }, restored.State.RpadDocument!.FrontMatter.Args);
        Assert.Equal(2, restored.State.RpadDocument.FrontMatter.References!.Count);
        Assert.Equal("1.2.3", restored.State.RpadDocument.FrontMatter.Packages["Example"].Version);
        Assert.Equal("net8.0", restored.State.RpadDocument.FrontMatter.TargetSdk.ToString());
        Assert.Equal("dirty named", next.Control(next.Vm.Tabs[1]).State.Editor.Text);
        Assert.All(next.Vm.Tabs, tab => Assert.True(tab.IsDirty));
        Assert.Equal(original, File.ReadAllBytes(path));
        await next.Close();
    }

    [AvaloniaFact]
    public async Task ExitCheckpoint_Should_WaitForSaveAsPathRebase_AndJoinRepeatedRequests()
    {
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var tab = session.Vm.Tabs.Single();
        session.Control(tab).State.Editor.Text = "draft";
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(answer.Task);
        var saving = session.Vm.SaveTabAsAsync(tab, tab.WriteToFile!);
        var duplicate = session.Vm.SaveTabAsAsync(tab, tab.WriteToFile!);
        Assert.Same(saving, duplicate);
        var checkpoint = session.Vm.CheckpointRecoveryAsync();
        Assert.False(checkpoint.IsCompleted);
        var path = Path.Combine(_root, "saved-as.rpad");
        answer.SetResult(path);
        await saving;
        Assert.True(await checkpoint);
        await session.Close();
        session.Store.Dispose();
        using var fresh = new DiskRecoveryStore(RecoveryRoot);
        var frame = Assert.Single((await fresh.LoadAsync())!.Tabs);
        Assert.Equal(path, frame.FilePath);
        Assert.False(frame.IsDirty);
        Assert.Equal("draft", frame.EditorText);
    }

    [AvaloniaFact]
    public async Task ExplicitDiscard_Should_RemoveDraftFromNextRestore_AndKeepEmptySessionEmpty()
    {
        using (var first = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await first.Ready();
            var tab = first.Vm.Tabs.Single();
            first.Control(tab).State.Editor.Text = "discard me";
            first.Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.DontSave);
            await ((IEditorTabActions)first.Vm).CloseAsync(tab);
            Assert.Empty(first.Vm.Tabs);
            await first.Close();
        }
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        Assert.Empty(next.Vm.Tabs);
        await next.Close();
    }

    [AvaloniaFact]
    public async Task FailedCheckpoint_Should_KeepWindowAndDirtyTextOpen_AndAllowRetry()
    {
        var fail = true;
        using var store = new DiskRecoveryStore(RecoveryRoot, step => { if (fail && step == "payload") throw new IOException("injected"); });
        using var session = new Session(_root, store);
        await session.Ready();
        var tab = session.Vm.Tabs.Single();
        session.Control(tab).State.Editor.Text = "keep me";
        session.Host.Close();
        await TestWait.WhileAsync(() => !session.Host.IsEnabled);
        Assert.True(session.Host.IsVisible);
        Assert.Equal("keep me", session.Control(tab).State.Editor.Text);
        Assert.True(tab.IsDirty);
        fail = false;
        await session.Close();
    }

    [AvaloniaFact]
    public async Task DisabledRecovery_Should_NotReadOrOverwriteOldDrafts()
    {
        var store = new Mock<IRecoveryStore>(MockBehavior.Strict);
        store.Setup(s => s.Dispose());
        using var session = new Session(_root, store.Object, enabled: false);
        await session.Ready();
        Assert.False(session.Vm.RestoresDrafts);
        await session.Close();
        store.Verify(s => s.LoadAsync(), Times.Never);
        store.Verify(s => s.CommitAsync(It.IsAny<RecoverySnapshot>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task CleanChangedAndDeletedFiles_Should_UseCurrentDiskOrRecoverTheMissingCopy()
    {
        var changed = Path.Combine(_root, "changed.rpad");
        var deleted = Path.Combine(_root, "deleted.rpad");
        File.WriteAllText(changed, RpadWriter.NewFile("old", "Changed"));
        File.WriteAllText(deleted, RpadWriter.NewFile("cached", "Deleted"));
        RecoveryTab Frame(string path)
        {
            var raw = File.ReadAllText(path);
            return new()
            {
                FilePath = path,
                EditorText = RpadParser.TryParse(raw)!.RunCode,
                Document = RpadParser.TryParse(raw)!.CaptureSnapshot(),
                BaselineKnown = true,
                DiskHash = ScriptFileState.Read(path).Hash
            };
        }
        using (var seed = new DiskRecoveryStore(RecoveryRoot))
            await seed.CommitAsync(new() { Revision = 1, Tabs = [Frame(changed), Frame(deleted)] });
        File.WriteAllText(changed, RpadWriter.NewFile("new on disk", "Changed"));
        File.Delete(deleted);
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        Assert.Equal("new on disk", next.Control(next.Vm.Tabs[0]).State.Editor.Text.Trim());
        Assert.False(next.Vm.Tabs[0].IsDirty);
        Assert.Equal("cached", next.Control(next.Vm.Tabs[1]).State.Editor.Text.Trim());
        Assert.True(next.Vm.Tabs[1].IsDirty);
        Assert.False(File.Exists(deleted));
        await next.Close();
        Assert.False(File.Exists(deleted));
    }

    [AvaloniaFact]
    public async Task Restart_Should_PreserveVisualBasicMarkdownAndInvalidRawText()
    {
        var doc = RpadParser.TryParse(RpadWriter.NewFile("Console.WriteLine(1)", "VB", language: RpadLanguage.Vb))!;
        var captured = doc.CaptureSnapshot();
        var before = "# Notes\r\n\r\n```vb run\r\n";
        var after = "\r\n```\r\nAfter **code**\r\n";
        captured = captured with { BodyBeforeCode = before, BodyAfterCode = after };
        var vb = new RecoveryTab
        {
            EditorText = "Console.WriteLine(2)\r\n",
            Document = captured,
            IsDirty = true,
            Title = "Example VB",
            CaretOffset = 8,
            SelectionStart = 2,
            SelectionLength = 6
        };
        var raw = new RecoveryTab { EditorText = "---\nlanguage: [broken\n${0}", IsDirty = true, ParseError = "Invalid header" };
        using (var seed = new DiskRecoveryStore(RecoveryRoot))
            await seed.CommitAsync(new() { Revision = 1, Tabs = [vb, raw], ActiveTab = vb.Id });
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var ctrl = session.Control(session.Vm.Tabs[0]);
        Assert.Equal(RpadLanguage.Vb, ctrl.State.RpadDocument!.FrontMatter.Language);
        Assert.Equal(before, ctrl.State.RpadDocument.CaptureSnapshot().BodyBeforeCode);
        Assert.Equal(after, ctrl.State.RpadDocument.CaptureSnapshot().BodyAfterCode);
        Assert.Equal("Example VB", session.Vm.Tabs[0].FileName);
        Assert.Equal(8, ctrl.State.Editor.CaretOffset);
        Assert.Equal(6, ctrl.State.Editor.SelectionLength);
        Assert.Equal(raw.EditorText, session.Control(session.Vm.Tabs[1]).State.Editor.Text);
        Assert.Null(session.Control(session.Vm.Tabs[1]).State.RpadDocument);
        Assert.NotNull(session.Control(session.Vm.Tabs[1]).State.DocumentParseError);
        await session.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrFailedSaveAs_Should_CheckpointTheUntitledDraft(bool writeFails)
    {
        using (var first = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await first.Ready();
            var tab = first.Vm.Tabs.Single();
            first.Control(tab).State.Editor.Text = "not lost";
            first.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(writeFails ? Path.Combine(_root, "missing", "file.rpad") : null);
            await first.Vm.SaveTabAsAsync(tab, tab.WriteToFile!);
            Assert.Null(tab.FilePath);
            Assert.True(tab.IsDirty);
            await first.Close();
        }
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        var restored = next.Vm.Tabs.Single();
        Assert.Null(restored.FilePath);
        Assert.Equal("not lost", next.Control(restored).State.Editor.Text);
        Assert.True(restored.IsDirty);
        await next.Close();
    }

    [AvaloniaFact]
    public async Task UnreadableRecovery_Should_FallBackToNormalCloseAndLeaveStoreUntouched()
    {
        var store = new Mock<IRecoveryStore>();
        store.Setup(s => s.LoadAsync()).ThrowsAsync(new InvalidDataException("broken"));
        using var session = new Session(_root, store.Object);
        await session.Ready();
        Assert.False(session.Vm.RestoresDrafts);
        var tab = session.Vm.Tabs.Single();
        session.Control(tab).State.Editor.Text = "legacy draft";
        session.Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.Cancel);
        session.Host.Close();
        await TestWait.WhileAsync(() => !session.Host.IsEnabled);
        Assert.True(session.Host.IsVisible);
        session.Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.DontSave);
        await session.Close();
        store.Verify(s => s.CommitAsync(It.IsAny<RecoverySnapshot>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task ClosingDuringStartup_Should_WaitForRestoredEditors_AndKeepTheDraft()
    {
        var source = new TaskCompletionSource<RecoverySnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new RecoveryTab { EditorText = "restored before exit", IsDirty = true };
        RecoverySnapshot? written = null;
        var store = new Mock<IRecoveryStore>();
        store.Setup(r => r.LoadAsync()).Returns(source.Task);
        store.Setup(r => r.CommitAsync(It.IsAny<RecoverySnapshot>())).Callback<RecoverySnapshot>(s => written = s).Returns(Task.CompletedTask);
        using var session = new Session(_root, store.Object);
        Assert.False(session.Host.IsEnabled);
        session.Host.Close();
        Assert.True(session.Host.IsVisible);
        source.SetResult(new() { Revision = 7, Tabs = [frame], ActiveTab = frame.Id });
        await TestWait.WhileAsync(() => session.Host.IsVisible);
        Assert.Equal("restored before exit", Assert.Single(written!.Tabs).EditorText);
        Assert.Equal(frame.Id, written.ActiveTab);
        Assert.Equal(8, written.Revision);
    }

    [AvaloniaFact]
    public async Task RepeatedClose_Should_CommitOnce_AndWaitForDurableCompletion()
    {
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IRecoveryStore>();
        store.Setup(r => r.LoadAsync()).ReturnsAsync((RecoverySnapshot?)null);
        store.Setup(r => r.CommitAsync(It.IsAny<RecoverySnapshot>())).Returns(committing.Task);
        using var session = new Session(_root, store.Object);
        await session.Ready();
        session.Control(session.Vm.Tabs.Single()).State.Editor.Text = "pending commit";
        session.Host.Close();
        session.Host.Close();
        Assert.True(session.Host.IsVisible);
        Assert.False(session.Host.IsEnabled);
        store.Verify(r => r.CommitAsync(It.IsAny<RecoverySnapshot>()), Times.Once);
        committing.SetResult();
        await TestWait.WhileAsync(() => session.Host.IsVisible);
    }

    [AvaloniaFact]
    public async Task RenameWaitingForConflictChoice_Should_FinishBeforeTheExitSnapshot()
    {
        var original = Path.Combine(_root, "before.rpad");
        File.WriteAllText(original, RpadWriter.NewFile("old", "Before"));
        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            session.Vm.OpenTab(original);
            await session.Ready();
            var tab = session.Vm.Tabs.Last();
            session.Control(tab).State.Editor.Text = "current code";
            File.WriteAllText(original, RpadWriter.NewFile("external", "Before"));
            var choice = new TaskCompletionSource<SaveConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).Returns(choice.Task);
            var renaming = session.Vm.RenameFile(original, "after");
            var checkpoint = session.Vm.CheckpointRecoveryAsync();
            Assert.False(checkpoint.IsCompleted);
            choice.SetResult(SaveConflictChoice.Overwrite);
            await renaming;
            Assert.True(await checkpoint);
            await session.Close();
        }
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        var renamed = next.Vm.Tabs.Single(t => t.FilePath is not null);
        Assert.Equal(Path.Combine(_root, "after.rpad"), renamed.FilePath);
        Assert.Equal("current code", next.Control(renamed).State.Editor.Text.Trim());
        Assert.False(File.Exists(original));
        await next.Close();
    }

    [AvaloniaFact]
    public async Task TwoSessions_Should_KeepTheirOwnDrafts_AndRestoreOnlyAnUnlockedSession()
    {
        using (var first = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await first.Ready();
            first.Control(first.Vm.Tabs.Single()).State.Editor.Text = "first instance";
            Assert.True(await first.Vm.CheckpointRecoveryAsync());
            using (var second = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
            {
                await second.Ready();
                Assert.Equal("", second.Control(second.Vm.Tabs.Single()).State.Editor.Text);
                second.Control(second.Vm.Tabs.Single()).State.Editor.Text = "second instance";
                await second.Close();
            }
            using (var third = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
            {
                await third.Ready();
                Assert.Equal("second instance", third.Control(third.Vm.Tabs.Single()).State.Editor.Text);
                await third.Close();
            }
            await first.Close();
        }
        using var final = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await final.Ready();
        Assert.Equal("first instance", final.Control(final.Vm.Tabs.Single()).State.Editor.Text);
        await final.Close();
    }

    public void Dispose() => Directory.Delete(_root, true);
}
