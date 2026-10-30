using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

public sealed class SessionDraftLifecycleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-session-drafts").FullName;
    private string RecoveryRoot => Path.Combine(_root, "recovery");

    private sealed class Session : IDisposable
    {
        public Mock<IWindowService> Windows { get; } = new();
        public MainWindowViewModel Vm { get; }
        public EditorTabsControl Tabs { get; }
        public MainWindow Host { get; }
        public IRecoveryStore Store { get; }
        // What the language was asked to run, and what the editor was told to analyze.
        public List<ExecutionRequest> Runs { get; } = [];
        public List<ExecutionRequest> EditorContexts { get; } = [];

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
            // Closing with unsaved tabs keeps them in the stash unless a test answers otherwise.
            Windows.Setup(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(ExitUnsavedChoice.KeepInStash);
            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
            var registry = new Mock<ILanguageHostRegistry>();
            var host = new Mock<ILanguageHost>();
            host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(RunResult.Exited(0));
            host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
            registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()))
                .Callback<string, ExecutionRequest>((_, r) => Runs.Add(r)).Returns(host.Object);
            var intelliSense = new Mock<IIntelliSenseService>();
            intelliSense.Setup(i => i.ApplyExecutionContext(It.IsAny<ExecutionRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ExecutionRequest, CancellationToken>((r, _) => EditorContexts.Add(r));
            var cache = new Mock<IBuildCacheService>();
            cache.Setup(c => c.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => Directory.CreateDirectory(Path.Combine(root, "build")).FullName);
            Vm = new([], files.Object, Windows.Object, prefs.Object, new LanguageHostRegistry(), recovery: store);
            Tabs = new(theme.Object, Windows.Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object, cache.Object);
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
        var path = Path.Combine(_root, "named.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("saved", "Named"));
        var original = File.ReadAllBytes(path);
        Guid active;
        using (var first = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await first.Ready();
            var blank = Assert.Single(first.Vm.Tabs);
            first.Control(blank).State.Editor.Text = "untitled\r\n${0}";
            var fm = first.Control(blank).State.MdRunDocument!.FrontMatter;
            fm.Arguments = [new() { Name = "values", Type = MdRunArgumentType.StringList, Default = MdRunValue.List(new[] { "", "a b", "Ж" }.Select(MdRunValue.Of)) }];
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
        Assert.Equal(2, next.Vm.Tabs.Count);
        Assert.Equal(new string?[] { null, path }, next.Vm.Tabs.Select(t => t.FilePath));
        Assert.Equal(active, next.Vm.ActiveTab!.RecoveryId);
        var restored = next.Control(next.Vm.Tabs[0]);
        Assert.Equal("untitled\r\n${0}", restored.State.Editor.Text);
        Assert.Equal(new[] { "", "a b", "Ж" }, restored.State.MdRunDocument!.FrontMatter.Arguments[0].Default!.Items!.Select(i => i.Text));
        Assert.Equal(2, restored.State.MdRunDocument.FrontMatter.References!.Count);
        Assert.Equal("1.2.3", restored.State.MdRunDocument.FrontMatter.Packages["Example"].Version);
        Assert.Equal("net8.0", restored.State.MdRunDocument.FrontMatter.TargetSdk.ToString());
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
        var path = Path.Combine(_root, "saved-as.inv");
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
        var changed = Path.Combine(_root, "changed.inv");
        var deleted = Path.Combine(_root, "deleted.inv");
        File.WriteAllText(changed, MdRunWriter.NewFile("old", "Changed"));
        File.WriteAllText(deleted, MdRunWriter.NewFile("cached", "Deleted"));
        RecoveryTab Frame(string path)
        {
            var raw = File.ReadAllText(path);
            return new()
            {
                FilePath = path,
                EditorText = MdRunParser.TryParse(raw)!.RunCode,
                Document = MdRunParser.TryParse(raw)!.CaptureSnapshot(),
                BaselineKnown = true,
                DiskHash = ScriptFileState.Read(path).Hash
            };
        }
        using (var seed = new DiskRecoveryStore(RecoveryRoot))
            await seed.CommitAsync(new() { Revision = 1, Tabs = [Frame(changed), Frame(deleted)] });
        File.WriteAllText(changed, MdRunWriter.NewFile("new on disk", "Changed"));
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
        var doc = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(1)", "VB", language: MdRunLanguage.Vb))!;
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
        Assert.Equal(MdRunLanguage.Vb, ctrl.State.MdRunDocument!.FrontMatter.Language);
        Assert.Equal(before, ctrl.State.MdRunDocument.CaptureSnapshot().BodyBeforeCode);
        Assert.Equal(after, ctrl.State.MdRunDocument.CaptureSnapshot().BodyAfterCode);
        Assert.Equal("Example VB", session.Vm.Tabs[0].FileName);
        Assert.Equal(8, ctrl.State.Editor.CaretOffset);
        Assert.Equal(6, ctrl.State.Editor.SelectionLength);
        Assert.Equal(raw.EditorText, session.Control(session.Vm.Tabs[1]).State.Editor.Text);
        Assert.Null(session.Control(session.Vm.Tabs[1]).State.MdRunDocument);
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
                .ReturnsAsync(writeFails ? Path.Combine(_root, "missing", "file.inv") : null);
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
        if (NativeMenu.GetMenu(session.Host) is { } native)
            Assert.All(native.Items.OfType<NativeMenuItem>(), item => Assert.False(item.IsEnabled));
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
        var original = Path.Combine(_root, "before.inv");
        File.WriteAllText(original, MdRunWriter.NewFile("old", "Before"));
        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            session.Vm.OpenTab(original);
            await session.Ready();
            var tab = session.Vm.Tabs.Last();
            session.Control(tab).State.Editor.Text = "current code";
            File.WriteAllText(original, MdRunWriter.NewFile("external", "Before"));
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
        Assert.Equal(Path.Combine(_root, "after.inv"), renamed.FilePath);
        Assert.Equal("current code", next.Control(renamed).State.Editor.Text.Trim());
        Assert.False(File.Exists(original));
        await next.Close();
    }

    [AvaloniaFact]
    public async Task TwoSessions_Should_KeepTheirOwnDrafts_AndALaterStartShouldRestoreBoth()
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
        // Neither window's draft is orphaned: the newer session is restored, the other one imported.
        using (var final = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await final.Ready();
            Assert.Equal(["first instance", "second instance"], final.Vm.Tabs.Select(t => final.Control(t).State.Editor.Text));
            Assert.Contains("another Invoklet window", final.Vm.RecoveryNotice);
            AssertNoModalNotice(final);
            await final.Close();
        }
        using var again = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await again.Ready();
        Assert.Equal(["first instance", "second instance"], again.Vm.Tabs.Select(t => again.Control(t).State.Editor.Text));
        Assert.Null(again.Vm.RecoveryNotice); // imported once, then simply part of this session
        await again.Close();
    }

    private static void AssertNoModalNotice(Session session) =>
        session.Windows.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    // A closed session of another window, written as that window's store would have left it.
    private async Task<string> ClosedSessionAsync(DateTime written, params RecoveryTab[] tabs)
    {
        var before = Directory.Exists(RecoveryRoot) ? Directory.GetDirectories(RecoveryRoot) : [];
        using (var store = new DiskRecoveryStore(RecoveryRoot))
        {
            // Own a fresh folder even when older closed sessions exist: hold those while creating it.
            var held = before.Select(f => File.Open(Path.Combine(f, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)).ToList();
            try { await store.CommitAsync(new RecoverySnapshot { Revision = 1, Tabs = tabs, ActiveTab = tabs.FirstOrDefault()?.Id }); }
            finally { held.ForEach(h => h.Dispose()); }
        }
        var folder = Directory.GetDirectories(RecoveryRoot).Single(f => !before.Contains(f));
        Directory.SetLastWriteTimeUtc(folder, written);
        return folder;
    }

    private static RecoveryTab DirtyFileDraft(string path, string text, string args)
    {
        var bytes = File.ReadAllBytes(path);
        var document = MdRunParser.Parse(MdRunWriter.NewFile(text, Path.GetFileNameWithoutExtension(path), kind: MdRunKind.Program)
            .Replace("kind: Program\n", $"kind: Program\narguments:\n  - name: value\n    default: \"{args}\"\n")).Document!;
        return new RecoveryTab
        {
            FilePath = path,
            EditorText = text,
            Document = document.CaptureSnapshot(),
            IsDirty = true,
            BaselineKnown = true,
            DiskHash = ScriptFileState.Of(bytes).Hash,
        };
    }

    [AvaloniaFact]
    public async Task TwoWindowsDraftsOfTheSameFile_Should_BothBeRestored_WithoutWritingTheFile()
    {
        var path = Path.Combine(_root, "shared.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("on disk", "Shared"));
        var disk = File.ReadAllBytes(path);
        var otherFolder = await ClosedSessionAsync(DateTime.UtcNow.AddMinutes(-10), DirtyFileDraft(path, "other window's edit", "other-arg"));
        await ClosedSessionAsync(DateTime.UtcNow, DirtyFileDraft(path, "this window's edit", "own-arg"));

        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            var file = session.Vm.Tabs.Single(t => t.FilePath is not null);
            Assert.Equal(path, file.FilePath);
            Assert.Equal("this window's edit", session.Control(file).State.Editor.Text);
            Assert.True(file.IsDirty);
            var copy = session.Vm.Tabs.Single(t => t.FilePath is null);
            Assert.Equal("shared (from another window)", copy.FileName);
            Assert.True(copy.IsDirty);
            var copyState = session.Control(copy).State;
            Assert.Equal("other window's edit", copyState.Editor.Text);
            Assert.Equal("other-arg", copyState.MdRunDocument!.FrontMatter.Arguments.Single().Default!.Text);
            Assert.Equal(MdRunKind.Program, copyState.MdRunDocument.FrontMatter.Kind);
            Assert.Equal("own-arg", session.Control(file).State.MdRunDocument!.FrontMatter.Arguments.Single().Default!.Text);
            Assert.Contains("untitled copy", session.Vm.RecoveryNotice);
            Assert.Equal(file, session.Vm.ActiveTab); // the restored session keeps its own active tab
            AssertNoModalNotice(session);
            await session.Close();
        }
        Assert.Equal(disk, File.ReadAllBytes(path));
        Assert.True(File.Exists(Path.Combine(otherFolder, "imported.json")));

        // The copy keeps its provenance in later checkpoints and is not imported again.
        using var again = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await again.Ready();
        Assert.Equal(2, again.Vm.Tabs.Count);
        Assert.Equal("shared (from another window)", again.Vm.Tabs.Single(t => t.FilePath is null).FileName);
        var captured = again.Control(again.Vm.Tabs.Single(t => t.FilePath is null)).CaptureRecovery();
        Assert.Equal(path, captured.OriginalPath);
        Assert.Equal(Path.GetFileName(otherFolder), captured.ImportedFrom);
        Assert.Null(again.Vm.RecoveryNotice);
        await again.Close();
        Assert.Equal(disk, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task AnImportedDraftOfAFileNotOpenHere_Should_ReopenItWithTheBaselineItWasEditedAgainst()
    {
        var path = Path.Combine(_root, "elsewhere.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("original", "Elsewhere"));
        var draft = DirtyFileDraft(path, "edited in the other window", "x");
        File.WriteAllText(path, MdRunWriter.NewFile("changed on disk later", "Elsewhere"));
        var changed = File.ReadAllBytes(path);
        await ClosedSessionAsync(DateTime.UtcNow.AddMinutes(-10), draft);
        await ClosedSessionAsync(DateTime.UtcNow, new RecoveryTab { EditorText = "own untitled", IsDirty = true });

        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var tab = session.Vm.Tabs.Single(t => t.FilePath == path);
        Assert.Equal("edited in the other window", session.Control(tab).State.Editor.Text);
        Assert.True(tab.IsDirty);
        // A save compares with what the draft was edited against, so the later disk change is noticed.
        Assert.Equal(draft.DiskHash, tab.DiskState!.Hash);
        Assert.False(tab.RequiresBaselineConfirmation);
        Assert.Equal("own untitled", session.Control(session.Vm.ActiveTab!).State.Editor.Text);
        await session.Close();
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task ADamagedOtherSession_Should_BeNamedInANonModalNotice_AndKeptUnchanged()
    {
        var damaged = await ClosedSessionAsync(DateTime.UtcNow.AddMinutes(-10), new RecoveryTab { EditorText = "lost?", IsDirty = true });
        var pointer = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(damaged, "current.json")))!;
        File.WriteAllText(Path.Combine(damaged, pointer["Current"]!.GetValue<string>()), "{ damaged");
        var files = Directory.GetFiles(damaged).Where(f => !f.EndsWith("owner.lock")).ToDictionary(f => f, File.ReadAllBytes);
        await ClosedSessionAsync(DateTime.UtcNow, new RecoveryTab { EditorText = "own draft", IsDirty = true });

        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            Assert.Equal("own draft", session.Control(session.Vm.Tabs.Single()).State.Editor.Text);
            Assert.Contains(damaged, session.Vm.RecoveryNotice);
            Assert.True(session.Host.FindControl<Border>("RecoveryNoticeBar")!.IsVisible);
            AssertNoModalNotice(session);
            session.Vm.DismissRecoveryNotice();
            Assert.False(session.Host.FindControl<Border>("RecoveryNoticeBar")!.IsVisible);
            await session.Close();
        }
        Assert.All(files, f => Assert.Equal(f.Value, File.ReadAllBytes(f.Key)));
    }

    [AvaloniaFact]
    public async Task UnreadableRecovery_Should_SayItNonModally()
    {
        var store = new Mock<IRecoveryStore>();
        store.Setup(s => s.LoadAsync()).ThrowsAsync(new InvalidDataException("broken"));
        using var session = new Session(_root, store.Object);
        await session.Ready();
        Assert.Contains("could not be read and have been kept unchanged", session.Vm.RecoveryNotice);
        AssertNoModalNotice(session);
        session.Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.DontSave);
        await session.Close();
    }

    private async Task<(Session Session, EditorTabItemViewModel Named, string Path)> DirtyNamedTabAsync(IRecoveryStore? store = null)
    {
        var path = Path.Combine(_root, "named.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("saved text", "Named"));
        var session = new Session(_root, store ?? new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        session.Vm.OpenTab(path);
        await session.Ready();
        var named = session.Vm.Tabs.Single(t => t.FilePath == path);
        session.Control(named).State.Editor.Text = "edited text";
        Assert.True(named.IsDirty);
        return (session, named, path);
    }

    private static void Answer(Session session, ExitUnsavedChoice choice) =>
        session.Windows.Setup(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(choice);

    private static async Task CloseAndSettle(Session session)
    {
        session.Host.Close();
        await TestWait.WhileAsync(() => !session.Host.IsEnabled);
    }

    [AvaloniaFact]
    public async Task ClosingWithNothingUnsaved_Should_NotAsk_AndStillKeepTheSession()
    {
        var path = Path.Combine(_root, "clean.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("clean", "Clean"));
        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            session.Vm.OpenTab(path);
            await session.Ready();
            await session.Close();
            session.Windows.Verify(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        }
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        Assert.Equal(path, next.Vm.Tabs.Single().FilePath);
        await next.Close();
    }

    [AvaloniaFact]
    public async Task CancelInTheExitDialog_Should_KeepTheAppOpen_WithEverythingUnsaved()
    {
        var store = new Mock<IRecoveryStore>();
        store.Setup(r => r.LoadAsync()).ReturnsAsync((RecoverySnapshot?)null);
        var (session, named, path) = await DirtyNamedTabAsync(store.Object);
        using (session)
        {
            Answer(session, ExitUnsavedChoice.Cancel);
            await CloseAndSettle(session);
            Assert.True(session.Host.IsVisible);
            Assert.True(named.IsDirty);
            Assert.Equal("edited text", session.Control(named).State.Editor.Text);
            Assert.Contains("saved text", File.ReadAllText(path));
            store.Verify(r => r.CommitAsync(It.IsAny<RecoverySnapshot>()), Times.Never);
            session.Windows.Verify(w => w.ShowExitUnsavedDialogAsync(It.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "named" }))), Times.Once);
            // Asked again on the next close.
            Answer(session, ExitUnsavedChoice.KeepInStash);
            await session.Close();
        }
    }

    [AvaloniaFact]
    public async Task KeepInStash_Should_CloseWithoutWritingTheFile_AndRestoreTheDraft()
    {
        var (session, _, path) = await DirtyNamedTabAsync();
        using (session) await session.Close();
        Assert.Contains("saved text", File.ReadAllText(path));
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        var tab = next.Vm.Tabs.Single(t => t.FilePath == path);
        Assert.Equal("edited text", next.Control(tab).State.Editor.Text);
        Assert.True(tab.IsDirty);
        await next.Close();
    }

    [AvaloniaFact]
    public async Task KeepInStash_WhenTheCheckpointFails_Should_KeepTheAppOpen()
    {
        var store = new Mock<IRecoveryStore>();
        store.Setup(r => r.LoadAsync()).ReturnsAsync((RecoverySnapshot?)null);
        store.Setup(r => r.CommitAsync(It.IsAny<RecoverySnapshot>())).ThrowsAsync(new IOException("disk full"));
        var (session, named, _) = await DirtyNamedTabAsync(store.Object);
        using (session)
        {
            await CloseAndSettle(session);
            Assert.True(session.Host.IsVisible);
            Assert.True(named.IsDirty);
            session.Windows.Verify(w => w.ShowErrorAsync("Drafts Not Saved", It.IsAny<string>()), Times.Once);
            store.Setup(r => r.CommitAsync(It.IsAny<RecoverySnapshot>())).Returns(Task.CompletedTask);
            await session.Close();
        }
    }

    [AvaloniaFact]
    public async Task SaveAll_Should_WriteTheFiles_ThenClose_AndReopenThemClean()
    {
        var (session, _, path) = await DirtyNamedTabAsync();
        using (session)
        {
            Answer(session, ExitUnsavedChoice.SaveAll);
            await session.Close();
        }
        Assert.Contains("edited text", File.ReadAllText(path));
        using var next = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await next.Ready();
        var tab = next.Vm.Tabs.Single(t => t.FilePath == path);
        Assert.False(tab.IsDirty);
        Assert.Equal("edited text", next.Control(tab).State.Editor.Text.Trim());
        await next.Close();
    }

    // Saves already made are kept; the untitled tab whose Save As was cancelled stays unsaved and open.
    [AvaloniaFact]
    public async Task SaveAll_WithACancelledSaveAs_Should_KeepTheAppOpen_AndTheEarlierSave()
    {
        var (session, named, path) = await DirtyNamedTabAsync();
        using (session)
        {
            session.Vm.NewMdRunFile();
            await session.Ready();
            var untitled = session.Vm.Tabs.Last();
            session.Control(untitled).State.Editor.Text = "untitled draft";
            session.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);
            Answer(session, ExitUnsavedChoice.SaveAll);
            await CloseAndSettle(session);
            Assert.True(session.Host.IsVisible);
            Assert.False(named.IsDirty);
            Assert.Contains("edited text", File.ReadAllText(path));
            Assert.True(untitled.IsDirty);
            Assert.Equal("untitled draft", session.Control(untitled).State.Editor.Text);
            Answer(session, ExitUnsavedChoice.KeepInStash);
            await session.Close();
        }
    }

    [AvaloniaFact]
    public async Task SaveAll_WithAFileChangedOnDisk_Should_AskAndOnCancel_KeepTheAppOpenAndTheFile()
    {
        var (session, named, path) = await DirtyNamedTabAsync();
        using (session)
        {
            File.WriteAllText(path, MdRunWriter.NewFile("changed elsewhere", "Named"));
            var external = File.ReadAllBytes(path);
            session.Windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(SaveConflictChoice.Cancel);
            Answer(session, ExitUnsavedChoice.SaveAll);
            await CloseAndSettle(session);
            Assert.True(session.Host.IsVisible);
            Assert.True(named.IsDirty);
            Assert.Equal(external, File.ReadAllBytes(path));
            session.Windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Once);
            Answer(session, ExitUnsavedChoice.KeepInStash);
            await session.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingAgain_WhileTheExitDialogIsOpen_Should_AskOnce()
    {
        var store = new Mock<IRecoveryStore>();
        store.Setup(r => r.LoadAsync()).ReturnsAsync((RecoverySnapshot?)null);
        var (session, _, _) = await DirtyNamedTabAsync(store.Object);
        using (session)
        {
            var answer = new TaskCompletionSource<ExitUnsavedChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Windows.Setup(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>())).Returns(answer.Task);
            session.Host.Close();
            await TestWait.WhileAsync(() => answer.Task.IsCompleted is false
                && session.Windows.Invocations.All(i => i.Method.Name != nameof(IWindowService.ShowExitUnsavedDialogAsync)));
            session.Host.Close();
            session.Host.Close();
            session.Windows.Verify(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>()), Times.Once);
            answer.SetResult(ExitUnsavedChoice.Cancel);
            await TestWait.WhileAsync(() => !session.Host.IsEnabled);
            Assert.True(session.Host.IsVisible);
            store.Verify(r => r.CommitAsync(It.IsAny<RecoverySnapshot>()), Times.Never);
            Answer(session, ExitUnsavedChoice.KeepInStash);
            await session.Close();
        }
    }

    [AvaloniaFact]
    public async Task WithRecoveryOff_Closing_Should_UseTheUsualPerTabQuestions()
    {
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot), enabled: false);
        await session.Ready();
        session.Control(session.Vm.Tabs.Single()).State.Editor.Text = "untitled";
        session.Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.DontSave);
        await session.Close();
        session.Windows.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Once);
        session.Windows.Verify(w => w.ShowExitUnsavedDialogAsync(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    // ── Reference anchors of a detached copy ─────────────────────────────────────

    private static readonly DotnetSdk Sdk = new(".NET", "net10.0", "10.0.0", ".NET 10.0");

    // Two windows' drafts of one file whose references are relative to its folder: lib/example.dll
    // and $(CurrentQueryPath)/lib/macro.dll, both real files there. The second becomes a copy.
    private async Task<(string Script, string Library, string Macro)> SameFileDraftsWithReferencesAsync()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "original")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        var library = Path.Combine(folder, "lib", "example.dll");
        var macro = Path.Combine(folder, "lib", "macro.dll");
        File.Copy(typeof(Assert).Assembly.Location, library);
        File.Copy(typeof(Assert).Assembly.Location, macro);
        var path = Path.Combine(folder, "shared.inv");
        File.WriteAllText(path, MdRunWriter.NewFile("on disk", "Shared"));
        RecoveryTab Draft(string text, string arg)
        {
            var draft = DirtyFileDraft(path, text, arg);
            var document = MdRunDocument.FromSnapshot(draft.Document!);
            document.FrontMatter.References = ["lib/example.dll", "$(CurrentQueryPath)/lib/macro.dll"];
            document.FrontMatter.Packages["Humanizer"] = new Package("2.14.1");
            return draft with { Document = document.CaptureSnapshot() };
        }
        await ClosedSessionAsync(DateTime.UtcNow.AddMinutes(-10), Draft("other window's edit", "other-arg"));
        await ClosedSessionAsync(DateTime.UtcNow, Draft("this window's edit", "own-arg"));
        return (path, library, macro);
    }

    private static EditorTabItemViewModel Copy(Session session) => session.Vm.Tabs.Single(t => t.FilePath is null);

    // The references the build and the editor would use for the copy, as they resolve them.
    private static async Task<IReadOnlyList<string>> BuildReferencesAsync(Session session)
    {
        var copy = Copy(session);
        session.Vm.ActiveTab = copy;
        session.Runs.Clear();
        await session.Control(copy).RunAsync(false, Sdk);
        var options = CompiledDotnetLanguageHostOptions.From(Assert.Single(session.Runs), "/runtime/Invoklet.Runtime.dll");
        Assert.Empty(options.ReferenceErrors);
        Assert.Null(options.CurrentQueryPath); // still an untitled script at run time
        return options.AssemblyReferences;
    }

    private static IReadOnlyList<string> EditorReferences(Session session) =>
        DotnetPackageReferenceContext.For(session.EditorContexts.Last() with { Platform = Sdk })!.AssemblyReferences ?? [];

    // The copy keeps resolving the original's relative and $(CurrentQueryPath) references for runs
    // and the editor, though it is untitled — and still after another restart.
    [AvaloniaFact]
    public async Task ADetachedCopy_Should_ResolveReferencesFromTheOriginalsFolder_AlsoAfterRestart()
    {
        var (_, library, macro) = await SameFileDraftsWithReferencesAsync();
        using (var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot)))
        {
            await session.Ready();
            Assert.Equal([library, macro], await BuildReferencesAsync(session));
            await TestWait.EventuallyAsync(() => Assert.Equal([library, macro], EditorReferences(session)));
            await session.Close();
        }
        using var again = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await again.Ready();
        Assert.Equal([library, macro], await BuildReferencesAsync(again));
        await TestWait.EventuallyAsync(() => Assert.Equal([library, macro], EditorReferences(again)));
        await again.Close();
    }

    // The first Save As to another folder writes references that reach the same files from there,
    // keeps the rest of the metadata, and never touches the original file.
    [AvaloniaFact]
    public async Task ADetachedCopysFirstSaveAs_Should_RebaseItsReferences_FromTheOriginalsFolder()
    {
        var (script, library, macro) = await SameFileDraftsWithReferencesAsync();
        var original = File.ReadAllBytes(script);
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "elsewhere", "deeper")).FullName, "copy.inv");
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var copy = Copy(session);
        session.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(target);

        await session.Vm.SaveTabAsAsync(copy, copy.WriteToFile!);

        Assert.Equal(target, copy.FilePath);
        var saved = MdRunParser.TryParse(File.ReadAllText(target))!;
        Assert.Equal([library, macro], ReferencePathResolver.Resolve(saved.FrontMatter.References!, target).Assemblies);
        Assert.StartsWith("$(CurrentQueryPath)/", saved.FrontMatter.References![1]);
        Assert.Equal("other-arg", saved.FrontMatter.Arguments.Single().Default!.Text);
        Assert.Equal("2.14.1", saved.FrontMatter.Packages["Humanizer"].Version);
        Assert.Equal(MdRunKind.Program, saved.FrontMatter.Kind);
        Assert.Equal(original, File.ReadAllBytes(script));
        await session.Close();
    }

    // A cancelled Save As changes nothing: the copy still resolves from the original's folder.
    [AvaloniaFact]
    public async Task ADetachedCopysCancelledSaveAs_Should_KeepItsReferenceAnchor()
    {
        var (_, library, macro) = await SameFileDraftsWithReferencesAsync();
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var copy = Copy(session);
        session.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

        await session.Vm.SaveTabAsAsync(copy, copy.WriteToFile!);

        Assert.Null(copy.FilePath);
        Assert.True(copy.IsDirty);
        Assert.Equal(["lib/example.dll", "$(CurrentQueryPath)/lib/macro.dll"], session.Control(copy).State.MdRunDocument!.FrontMatter.References);
        Assert.Equal([library, macro], await BuildReferencesAsync(session));
        await session.Close();
    }

    // Saving the copy over the file it was detached from would overwrite the other version unasked.
    [AvaloniaFact]
    public async Task ADetachedCopy_Should_NotBeSavedOverItsOriginal()
    {
        var (script, _, _) = await SameFileDraftsWithReferencesAsync();
        var original = File.ReadAllBytes(script);
        using var session = new Session(_root, new DiskRecoveryStore(RecoveryRoot));
        await session.Ready();
        var copy = Copy(session);
        session.Windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(script);

        await session.Vm.SaveTabAsAsync(copy, copy.WriteToFile!);

        Assert.Null(copy.FilePath);
        Assert.True(copy.IsDirty);
        Assert.Equal(original, File.ReadAllBytes(script));
        session.Windows.Verify(w => w.ShowErrorAsync("Save Failed", It.Is<string>(m => m.Contains("shared.inv"))), Times.Once);
        await session.Close();
    }

    public void Dispose() => Directory.Delete(_root, true);
}
