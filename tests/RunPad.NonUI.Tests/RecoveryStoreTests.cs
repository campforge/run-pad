using System.Text.Json;
using RunPad.Services.Recovery;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-recovery-tests").FullName;
    private static RecoverySnapshot Snapshot(long revision, string text = "draft\r\nЖ\n${0}")
    {
        var tab = new RecoveryTab { EditorText = text, IsDirty = true, BaselineKnown = true, DiskHash = [1, 2, 3] };
        return new RecoverySnapshot { Revision = revision, Tabs = [tab], ActiveTab = tab.Id };
    }

    private sealed class TrackingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            base.Post(callback, state);
        }
    }

    [Fact]
    public async Task AnUnsupportedOtherSnapshot_Should_NotDisableTheReadableSession()
    {
        string draftFolder;
        using (var draft = new DiskRecoveryStore(_root))
        {
            await draft.CommitAsync(Snapshot(1, "readable"));
            draftFolder = Assert.Single(Directory.GetDirectories(_root));
            using var other = new DiskRecoveryStore(_root);
            await other.CommitAsync(Snapshot(1, "future"));
        }
        var otherFolder = Directory.GetDirectories(_root).Single(p => p != draftFolder);
        var payload = Assert.Single(Directory.GetFiles(otherFolder, "*.snapshot"));
        var future = File.ReadAllText(payload).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99");
        File.WriteAllText(payload, future);
        Directory.SetLastWriteTimeUtc(draftFolder, DateTime.UtcNow.AddMinutes(1));
        using var reopened = new DiskRecoveryStore(_root);
        Assert.Equal("readable", (await reopened.LoadAsync())!.Tabs[0].EditorText);
        Assert.Contains(otherFolder, reopened.Warning!);
        Assert.Equal(future, File.ReadAllText(payload));
    }

    [Fact]
    public async Task AnEmptyOtherSession_Should_NotProduceARecoveryWarning()
    {
        string draftFolder;
        using (var draft = new DiskRecoveryStore(_root))
        {
            await draft.CommitAsync(Snapshot(1));
            draftFolder = Assert.Single(Directory.GetDirectories(_root));
            using var empty = new DiskRecoveryStore(_root);
            await empty.CommitAsync(new RecoverySnapshot { Revision = 1, Tabs = [] });
        }
        for (var start = 0; start < 3; start++)
        {
            Directory.SetLastWriteTimeUtc(draftFolder, DateTime.UtcNow.AddMinutes(1));
            using var reopened = new DiskRecoveryStore(_root);
            Assert.NotEmpty((await reopened.LoadAsync())!.Tabs);
            Assert.Null(reopened.Warning);
        }
    }

    [Fact]
    public async Task Commit_Should_NotDependOnTheCallersSynchronizationContext()
    {
        using var release = new ManualResetEventSlim();
        using var store = new DiskRecoveryStore(_root, stage =>
        {
            if (stage == "payload") release.Wait(TestContext.Current.CancellationToken);
        });
        await store.LoadAsync();
        var original = SynchronizationContext.Current;
        var context = new TrackingContext();
        Task commit;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            commit = store.CommitAsync(Snapshot(1));
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); release.Set(); }
        await commit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, context.Posts);
    }

    // Two sessions closed with drafts (two windows ran at once); the later start keeps the newer as
    // its own and imports the other's drafts, leaving that session's files exactly as they were.
    private async Task<(string Own, string Other)> TwoClosedSessionsAsync(string ownText = "own draft", string otherText = "other draft")
    {
        string other;
        using (var first = new DiskRecoveryStore(_root))
        {
            await first.CommitAsync(Snapshot(1, otherText));
            other = Assert.Single(Directory.GetDirectories(_root));
            using var second = new DiskRecoveryStore(_root);
            await second.CommitAsync(Snapshot(1, ownText));
        }
        var own = Directory.GetDirectories(_root).Single(p => p != other);
        Directory.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddMinutes(1));
        return (own, other);
    }

    private static Dictionary<string, byte[]> Files(string folder) => Directory.GetFiles(folder)
        .Where(f => Path.GetFileName(f) != "owner.lock")
        .ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    [Fact]
    public async Task OtherUnlockedSessions_Should_BeImported_WithoutChangingTheirData()
    {
        var (own, other) = await TwoClosedSessionsAsync();
        var original = Files(other);
        using (var next = new DiskRecoveryStore(_root))
        {
            Assert.Equal("own draft", (await next.LoadAsync())!.Tabs[0].EditorText);
            Assert.Null(next.Warning);
            var import = Assert.Single(next.Imports);
            Assert.Equal(Path.GetFileName(other), import.Source);
            var tab = Assert.Single(import.Tabs);
            Assert.Equal("other draft", tab.EditorText);
            Assert.Equal(Path.GetFileName(other), tab.ImportedFrom);
            // Not consumed until a checkpoint holds the draft; meanwhile nobody else can take it.
            Assert.Equal(original, Files(other));
            Assert.Throws<IOException>(() => File.Open(Path.Combine(other, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
            await next.CommitAsync(Snapshot(2, "own draft") with { Tabs = [Snapshot(2).Tabs[0] with { EditorText = "own draft" }, tab] }
                is var s ? s with { ActiveTab = s.Tabs[0].Id } : null!);
        }
        var after = Files(other);
        Assert.True(after.Remove("imported.json"));
        Assert.Equal(original, after); // only a marker was added, nothing changed or deleted
        Assert.Equal(original.Keys.Order(), Files(other).Keys.Where(k => k != "imported.json").Order());

        using var third = new DiskRecoveryStore(_root);
        var restored = await third.LoadAsync();
        Assert.Equal(["own draft", "other draft"], restored!.Tabs.Select(t => t.EditorText));
        Assert.Empty(third.Imports);
        Assert.Null(third.Warning);
    }

    [Fact]
    public async Task ACrashBetweenTheImportingCheckpointAndTheMarker_Should_NotImportTwice()
    {
        var (own, other) = await TwoClosedSessionsAsync();
        using (var next = new DiskRecoveryStore(_root, stage => { if (stage == "committed") throw new IOException("crash"); }))
        {
            await next.LoadAsync();
            var tab = Assert.Single(Assert.Single(next.Imports).Tabs);
            var snapshot = new RecoverySnapshot { Revision = 2, Tabs = [tab], ActiveTab = tab.Id };
            await Assert.ThrowsAsync<IOException>(() => next.CommitAsync(snapshot));
        }
        Assert.False(File.Exists(Path.Combine(other, "imported.json")));
        // Even when the source session looks newest, the durable checkpoint's record wins.
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(5));
        using var again = new DiskRecoveryStore(_root);
        Assert.Equal(["other draft"], (await again.LoadAsync())!.Tabs.Select(t => t.EditorText));
        Assert.Empty(again.Imports);
        Assert.True(File.Exists(Path.Combine(other, "imported.json")));
    }

    [Fact]
    public async Task AnImportNeverCheckpointed_Should_BeOfferedAgain()
    {
        var (_, other) = await TwoClosedSessionsAsync();
        using (var next = new DiskRecoveryStore(_root)) Assert.Single((await next.LoadAsync()) is not null ? next.Imports : []);
        using var again = new DiskRecoveryStore(_root);
        await again.LoadAsync();
        Assert.Equal("other draft", Assert.Single(Assert.Single(again.Imports).Tabs).EditorText);
        Assert.False(File.Exists(Path.Combine(other, "imported.json")));
    }

    [Fact]
    public async Task ADiscardedImportedDraft_Should_NotComeBack()
    {
        await TwoClosedSessionsAsync();
        using (var next = new DiskRecoveryStore(_root))
        {
            await next.LoadAsync();
            Assert.Single(next.Imports);
            await next.CommitAsync(new RecoverySnapshot { Revision = 2, Tabs = [] }); // the user discarded it
        }
        for (var i = 0; i < 2; i++)
        {
            using var later = new DiskRecoveryStore(_root);
            Assert.Empty((await later.LoadAsync())!.Tabs);
            Assert.Empty(later.Imports);
            Assert.Null(later.Warning);
        }
    }

    [Fact]
    public async Task ALiveSession_Should_NeverBeImported()
    {
        using var live = new DiskRecoveryStore(_root);
        await live.CommitAsync(Snapshot(1, "live draft"));
        var liveFolder = Assert.Single(Directory.GetDirectories(_root));
        var original = Files(liveFolder);
        using var next = new DiskRecoveryStore(_root);
        Assert.Null(await next.LoadAsync());
        Assert.Empty(next.Imports);
        Assert.Null(next.Warning);
        Assert.Equal(original, Files(liveFolder));
    }

    [Fact]
    public async Task AnOtherSessionWithOnlyCleanTabs_Should_NeitherBeImportedNorReported()
    {
        string other;
        using (var first = new DiskRecoveryStore(_root))
        {
            var clean = new RecoveryTab { FilePath = Path.Combine(_root, "clean.rpad"), EditorText = "saved", IsDirty = false };
            await first.CommitAsync(new RecoverySnapshot { Revision = 1, Tabs = [clean, new RecoveryTab { EditorText = "" }] });
            other = Assert.Single(Directory.GetDirectories(_root));
            using var second = new DiskRecoveryStore(_root);
            await second.CommitAsync(Snapshot(1, "own"));
        }
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-5));
        var original = Files(other);
        for (var i = 0; i < 3; i++)
        {
            using var next = new DiskRecoveryStore(_root);
            Assert.Equal("own", (await next.LoadAsync())!.Tabs[0].EditorText);
            Assert.Empty(next.Imports);
            Assert.Null(next.Warning);
        }
        Assert.Equal(original, Files(other));
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("newer-schema")]
    public async Task AnUnreadableOtherSession_Should_BeReportedAndKeptUnchanged(string damage)
    {
        var (own, other) = await TwoClosedSessionsAsync();
        var pointer = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(other, "current.json")))!;
        var payload = Path.Combine(other, pointer["Current"]!.GetValue<string>());
        if (damage == "damaged") File.WriteAllText(payload, "{ not json");
        else File.WriteAllText(payload, File.ReadAllText(payload).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"));
        var original = Files(other);
        for (var i = 0; i < 2; i++)
        {
            using var next = new DiskRecoveryStore(_root);
            Assert.Equal("own draft", (await next.LoadAsync())!.Tabs[0].EditorText);
            Assert.Empty(next.Imports);
            Assert.Contains(other, next.Warning!);
            await next.CommitAsync(Snapshot(2 + i, "own draft"));
        }
        Assert.Equal(original, Files(other));
    }

    [Fact]
    public async Task UnknownVersion_Should_KeepDataAndRefuseToOverwriteIt()
    {
        using (var store = new DiskRecoveryStore(_root)) await store.CommitAsync(Snapshot(1));
        var pointer = Path.Combine(Assert.Single(Directory.GetDirectories(_root)), "current.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 99, Current = "unknown", Previous = (string?)null });
        File.WriteAllBytes(pointer, bytes);
        using var next = new DiskRecoveryStore(_root);
        await Assert.ThrowsAsync<NotSupportedException>(() => next.LoadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => next.CommitAsync(Snapshot(2)));
        Assert.Equal(bytes, File.ReadAllBytes(pointer));
    }

    [Theory]
    [InlineData("missing-payload")]
    [InlineData("null-tab")]
    [InlineData("missing-current")]
    public async Task DamagedNewestCheckpoint_Should_RecoverPreviousWithoutOverwritingIt(string damage)
    {
        using (var store = new DiskRecoveryStore(_root))
        {
            await store.CommitAsync(Snapshot(1, "earlier"));
            await store.CommitAsync(Snapshot(2, "newest"));
        }
        var folder = Directory.GetDirectories(_root).Single();
        var pointerPath = Path.Combine(folder, "current.json");
        var pointer = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pointerPath))!;
        var payloadPath = Path.Combine(folder, pointer["Current"]!.GetValue<string>());
        if (damage == "missing-payload") File.Delete(payloadPath);
        else if (damage == "missing-current")
        {
            pointer["Current"] = null;
            File.WriteAllText(pointerPath, pointer.ToJsonString());
        }
        else
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(payloadPath))!;
            payload["Tabs"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
            File.WriteAllText(payloadPath, payload.ToJsonString());
        }
        var damagedPointer = File.ReadAllBytes(pointerPath);
        using var next = new DiskRecoveryStore(_root);
        Assert.Equal("earlier", (await next.LoadAsync())!.Tabs.Single().EditorText);
        Assert.NotNull(next.Warning);
        Assert.Equal(damagedPointer, File.ReadAllBytes(pointerPath));
    }

    [Fact]
    public async Task DisposeDuringLoad_Should_ReleaseLateAcquiredSessionOwnership()
    {
        using (var initial = new DiskRecoveryStore(_root)) await initial.CommitAsync(Snapshot(1));
        var cancellation = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new DiskRecoveryStore(_root, stage =>
        {
            if (stage != "locked") return;
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(5), cancellation);
        });
        var loading = store.LoadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        var disposing = Task.Run(store.Dispose, cancellation);
        try
        {
            Assert.False(disposing.IsCompleted);
        }
        finally { release.Set(); }
        await loading;
        await disposing;
        using var next = new DiskRecoveryStore(_root);
        Assert.NotNull(await next.LoadAsync());
        Assert.Single(Directory.GetDirectories(_root));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.LoadAsync());
    }

    public void Dispose() => Directory.Delete(_root, true);

    // The destination's newest checkpoint holds the imported draft; damage it so the start falls back
    // to the generation before the import. Returns (destination, source).
    private async Task<(string Destination, string Source)> ImportThenAsync(Func<string, Task>? afterImport = null)
    {
        var (own, other) = await TwoClosedSessionsAsync("BASE", "IMPORTED-DRAFT");
        using (var next = new DiskRecoveryStore(_root))
        {
            var restored = (await next.LoadAsync())!;
            var import = Assert.Single(next.Imports);
            await next.CommitAsync(new RecoverySnapshot { Revision = 2, Tabs = [.. restored.Tabs, .. import.Tabs] });
        }
        Assert.True(File.Exists(Path.Combine(other, "imported.json")));
        if (afterImport is not null) await afterImport(own);
        return (own, other);
    }

    private static void DamageCurrentPayload(string folder)
    {
        var pointer = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "current.json")))!;
        File.WriteAllText(Path.Combine(folder, pointer["Current"]!.GetValue<string>()), "broken snapshot");
    }

    // A marker alone is not proof the drafts are still reachable: when the importing checkpoint is
    // lost and the destination falls back to a generation from before the import, the source is
    // offered again, its files unchanged.
    [Fact]
    public async Task AnImportLostWithTheDestinationsNewestCheckpoint_Should_BeOfferedAgain()
    {
        var (destination, source) = await ImportThenAsync();
        var sourceFiles = Files(source);
        DamageCurrentPayload(destination);

        using var reopened = new DiskRecoveryStore(_root);
        var restored = (await reopened.LoadAsync())!;
        Assert.Equal(["BASE"], restored.Tabs.Select(t => t.EditorText));
        Assert.Equal("IMPORTED-DRAFT", Assert.Single(Assert.Single(reopened.Imports).Tabs).EditorText);
        Assert.Equal(sourceFiles, Files(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairingAnImportMarker_Should_NameTheCheckpointOwner_RegardlessOfSessionOrder(bool unrelatedFirst)
    {
        var (destination, source) = await ImportThenAsync();
        if (unrelatedFirst)
        {
            var staging = Path.Combine(_root, "staging");
            using (var unrelated = new DiskRecoveryStore(staging))
                await unrelated.CommitAsync(Snapshot(1, "UNRELATED"));
            var folder = Directory.GetDirectories(staging).Single();
            var moved = Path.Combine(_root, Path.GetFileName(folder));
            Directory.Move(folder, moved);
            Directory.SetLastWriteTimeUtc(moved, DateTime.UtcNow.AddMinutes(20));
        }
        Directory.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(10));
        using (var intermediate = new DiskRecoveryStore(_root))
            await intermediate.LoadAsync();

        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "imported.json")));
        Assert.Equal(Path.GetFileName(destination), marker.RootElement.GetProperty("Into").GetString());
        DamageCurrentPayload(destination);
        using var reopened = new DiskRecoveryStore(_root);
        var snapshot = await reopened.LoadAsync();
        Assert.Contains((snapshot?.Tabs ?? []).Concat(reopened.Imports.SelectMany(i => i.Tabs)),
            t => t.EditorText == "IMPORTED-DRAFT");
    }

    // Damage after a discard: the fallback generation still records the import (and holds the draft
    // itself), so the draft comes back once, from the destination, not a second time from the source.
    [Fact]
    public async Task AFallbackThatStillHoldsTheImport_Should_NotImportItAgain()
    {
        var (destination, _) = await ImportThenAsync(async own =>
        {
            using var later = new DiskRecoveryStore(_root);
            var restored = (await later.LoadAsync())!;
            await later.CommitAsync(new RecoverySnapshot { Revision = 3, Tabs = [restored.Tabs[0]] }); // discards the draft
        });
        DamageCurrentPayload(destination);

        using var reopened = new DiskRecoveryStore(_root);
        var restored = (await reopened.LoadAsync())!;
        Assert.Equal(["BASE", "IMPORTED-DRAFT"], restored.Tabs.Select(t => t.EditorText));
        Assert.Empty(reopened.Imports);
    }

    // The discard itself, with the destination intact, still holds on later starts.
    [Fact]
    public async Task ADiscardedImport_Should_StayDiscarded_WhileTheDestinationIsReadable()
    {
        await ImportThenAsync(async own =>
        {
            using var later = new DiskRecoveryStore(_root);
            var restored = (await later.LoadAsync())!;
            await later.CommitAsync(new RecoverySnapshot { Revision = 3, Tabs = [restored.Tabs[0]] });
        });
        for (var i = 0; i < 2; i++)
        {
            using var reopened = new DiskRecoveryStore(_root);
            Assert.Equal(["BASE"], (await reopened.LoadAsync())!.Tabs.Select(t => t.EditorText));
            Assert.Empty(reopened.Imports);
        }
    }

    // The destination session is gone altogether (its folder was removed): the source is offered again.
    [Fact]
    public async Task AMissingDestination_Should_LetTheSourceBeOfferedAgain()
    {
        var (destination, source) = await ImportThenAsync();
        Directory.Move(destination, Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-moved-" + Guid.NewGuid().ToString("N")));

        using var reopened = new DiskRecoveryStore(_root);
        Assert.Null(await reopened.LoadAsync());
        Assert.Equal("IMPORTED-DRAFT", Assert.Single(Assert.Single(reopened.Imports).Tabs).EditorText);
        Assert.True(File.Exists(Path.Combine(source, "imported.json")));
    }

    // A destination that can't be read at all may still hold the drafts: the source is neither
    // imported nor hidden — it is kept unchanged and named in the warning.
    [Fact]
    public async Task AnUnreadableDestination_Should_KeepTheSourceUnchanged_AndReportIt()
    {
        var (destination, source) = await ImportThenAsync();
        var sourceFiles = Files(source);
        foreach (var payload in Directory.GetFiles(destination, "*.snapshot")) File.WriteAllText(payload, "broken");

        using var reopened = new DiskRecoveryStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadAsync());
        Assert.Empty(reopened.Imports);
        Assert.Contains(destination, reopened.Warning!);
        Assert.Contains(source, reopened.Warning!);
        Assert.Equal(sourceFiles, Files(source));
    }

    // While the destination window is still running it owns the imported drafts: never import them twice.
    [Fact]
    public async Task ASourceWhoseDestinationIsLive_Should_NotBeImported()
    {
        var (destination, source) = await ImportThenAsync();
        DamageCurrentPayload(destination);
        using var live = File.Open(Path.Combine(destination, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using var reopened = new DiskRecoveryStore(_root);
        Assert.Null(await reopened.LoadAsync());
        Assert.Empty(reopened.Imports);
    }

    // Imported again after the loss, into a new session: the marker now names that session, and once
    // it records the import the source stays taken over, also while that session is live.
    [Fact]
    public async Task ASecondImport_Should_UpdateTheMarker_AndHoldWhileTheNewDestinationIsLive()
    {
        var (destination, source) = await ImportThenAsync();
        Directory.Move(destination, Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-moved-" + Guid.NewGuid().ToString("N")));
        string second;
        using (var reopened = new DiskRecoveryStore(_root))
        {
            Assert.Null(await reopened.LoadAsync());
            var import = Assert.Single(reopened.Imports);
            await reopened.CommitAsync(new RecoverySnapshot { Revision = 1, Tabs = import.Tabs });
            second = Assert.Single(Directory.GetDirectories(_root), f => f != source);
        }
        var marker = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(source, "imported.json")))!;
        Assert.Equal(Path.GetFileName(second), marker["Into"]!.GetValue<string>());
        Assert.NotEqual(Path.GetFileName(destination), marker["Into"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(source, "*.tmp"));

        using var live = new DiskRecoveryStore(_root);
        Assert.Equal(["IMPORTED-DRAFT"], (await live.LoadAsync())!.Tabs.Select(t => t.EditorText));
        Assert.Empty(live.Imports);
        using var other = new DiskRecoveryStore(_root);
        Assert.Null(await other.LoadAsync());
        Assert.Empty(other.Imports); // the live window holds them
    }

    // Once the checkpoint that imported a session is replaced, both generations a start can fall back
    // to hold its drafts: the source checkpoints are removed and later checkpoints stop recording it.
    [Fact]
    public async Task ASettledImport_Should_RetireTheSourceSession_AndStopRecordingIt()
    {
        var (_, source) = await ImportThenAsync();
        Assert.True(Directory.Exists(source)); // the importing checkpoint alone keeps it

        using (var later = new DiskRecoveryStore(_root))
        {
            var restored = (await later.LoadAsync())!;
            await later.CommitAsync(restored with { Revision = 3 });
            Assert.Equal(["owner.lock"], Directory.GetFiles(source).Select(Path.GetFileName));
            await later.CommitAsync(restored with { Revision = 4 });
        }
        using var reopened = new DiskRecoveryStore(_root);
        var snapshot = (await reopened.LoadAsync())!;
        Assert.Equal(["BASE", "IMPORTED-DRAFT"], snapshot.Tabs.Select(t => t.EditorText));
        Assert.Empty(snapshot.ImportedSessions ?? []);
        Assert.Empty(reopened.Imports);
        Assert.Null(reopened.Warning);
        Assert.Equal(2, Directory.GetDirectories(_root).Length);
    }

    // A session imported in turn carries the sessions it had imported: deleting it must not make
    // their drafts look abandoned, so their checkpoints are retired with it, never offered twice.
    [Fact]
    public async Task ImportingAnImportingSession_Should_TakeOverItsSources_AndRetireThemTogether()
    {
        var (destination, source) = await ImportThenAsync();
        var staging = Path.Combine(_root, "staging");
        using (var newest = new DiskRecoveryStore(staging))
            await newest.CommitAsync(Snapshot(1, "NEWEST"));
        var newestFolder = Path.Combine(_root, Path.GetFileName(Directory.GetDirectories(staging).Single()));
        Directory.Move(Directory.GetDirectories(staging).Single(), newestFolder);
        Directory.Delete(staging);
        Directory.SetLastWriteTimeUtc(newestFolder, DateTime.UtcNow.AddMinutes(10));

        using (var next = new DiskRecoveryStore(_root))
        {
            var restored = (await next.LoadAsync())!;
            Assert.Equal(["NEWEST"], restored.Tabs.Select(t => t.EditorText));
            var import = Assert.Single(next.Imports);
            Assert.Equal(Path.GetFileName(destination), import.Source);
            var merged = restored with { Revision = 2, Tabs = [.. restored.Tabs, .. import.Tabs] };
            await next.CommitAsync(merged);
            await next.CommitAsync(merged with { Revision = 3 });
        }
        Assert.Equal(["owner.lock"], Directory.GetFiles(destination).Select(Path.GetFileName));
        Assert.Equal(["owner.lock"], Directory.GetFiles(source).Select(Path.GetFileName));

        using var reopened = new DiskRecoveryStore(_root);
        var snapshot = (await reopened.LoadAsync())!;
        Assert.Equal(["NEWEST", "BASE", "IMPORTED-DRAFT"], snapshot.Tabs.Select(t => t.EditorText));
        Assert.Empty(reopened.Imports);
        Assert.Null(reopened.Warning);
    }

    [Fact]
    public async Task RetiringAnImport_Should_NotDeleteDataWrittenByTheNextOwner()
    {
        var (_, source) = await ImportThenAsync();
        var newPayload = Path.Combine(source, "new-owner.snapshot");
        FileStream? newOwner = null;
        try
        {
            using var later = new DiskRecoveryStore(_root, stage =>
            {
                if (stage != "import-retired") return;
                newOwner = new FileStream(Path.Combine(source, "owner.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                File.WriteAllText(newPayload, "new owner's draft");
            });
            var restored = (await later.LoadAsync())!;
            await later.CommitAsync(restored with { Revision = 3 });

            Assert.NotNull(newOwner);
            Assert.True(File.Exists(newPayload));
            Assert.Equal("new owner's draft", File.ReadAllText(newPayload));
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(source, "owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        finally { newOwner?.Dispose(); }
    }

    // The checkpoint that retires a source still records it. An instance that listed the folder
    // before and adopted it afterwards, empty, starts its own revisions again: its drafts must be
    // restored, not taken for the retired import, and never deleted as one.
    [Fact]
    public async Task AFolderAdoptedAfterRetiring_Should_KeepTheNewOwnersDrafts()
    {
        var (destination, source) = await ImportThenAsync();
        using (var later = new DiskRecoveryStore(_root))
        {
            var restored = (await later.LoadAsync())!;
            await later.CommitAsync(restored with { Revision = 3 }); // retires the source, then exits
        }
        Assert.Equal(["owner.lock"], Directory.GetFiles(source).Select(Path.GetFileName));
        var staging = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-adopter");
        using (var adopter = new DiskRecoveryStore(staging)) await adopter.CommitAsync(Snapshot(1, "ADOPTER-DRAFT"));
        foreach (var file in Directory.GetFiles(Directory.GetDirectories(staging).Single()).Where(f => Path.GetFileName(f) != "owner.lock"))
            File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        Directory.Delete(staging, true);
        Directory.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(10));
        var adopted = Files(source);

        using (var next = new DiskRecoveryStore(_root))
        {
            var own = (await next.LoadAsync())!;
            Assert.Equal(["BASE", "IMPORTED-DRAFT"], own.Tabs.Select(t => t.EditorText));
            var import = Assert.Single(next.Imports);
            Assert.Equal("ADOPTER-DRAFT", Assert.Single(import.Tabs).EditorText);
            var merged = own with { Revision = 4, Tabs = [.. own.Tabs, .. import.Tabs] };
            await next.CommitAsync(merged);
            // The old record no longer describes the folder: it is dropped, the adopter's data kept.
            Assert.Equal(adopted.Keys.Order(), Files(source).Keys.Where(k => k != "imported.json").Order());
            await next.CommitAsync(merged with { Revision = 5 });
        }
        using var reopened = new DiskRecoveryStore(_root);
        Assert.Equal(["BASE", "IMPORTED-DRAFT", "ADOPTER-DRAFT"], (await reopened.LoadAsync())!.Tabs.Select(t => t.EditorText));
        Assert.Empty(reopened.Imports);
    }

    // Checkpoints written before records named the imported checkpoint file still match by revision.
    [Fact]
    public async Task ARecordWithoutACheckpointFile_Should_StillMarkTheSourceAsTakenOver()
    {
        var (destination, source) = await ImportThenAsync();
        var pointer = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "current.json")))!;
        var payload = Path.Combine(destination, pointer["Current"]!.GetValue<string>());
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(payload))!;
        foreach (var record in snapshot["ImportedSessions"]!.AsArray())
            Assert.True(record!.AsObject().Remove("Payload"));
        File.WriteAllText(payload, snapshot.ToJsonString());
        File.Delete(Path.Combine(source, "imported.json"));
        Directory.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(10));

        using var reopened = new DiskRecoveryStore(_root);
        Assert.Equal(["BASE", "IMPORTED-DRAFT"], (await reopened.LoadAsync())!.Tabs.Select(t => t.EditorText));
        Assert.Empty(reopened.Imports);
        Assert.True(File.Exists(Path.Combine(source, "imported.json")));
    }
}
