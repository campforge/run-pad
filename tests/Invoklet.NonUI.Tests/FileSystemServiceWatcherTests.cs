using System.Threading.Channels;
using Invoklet.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The Queries tree is refreshed from FileSystemService.WatcherEvent. Folders created, renamed, moved
// or deleted outside Invoklet (Finder, a terminal, Obsidian) must reach it — including folders whose
// names contain a dot, such as project.v2 — while files that never show in the tree (notes.md, the
// files inside .obsidian) should not trigger refreshes. These tests run the real service's watcher on
// a temporary folder; each waits for an event with a bounded timeout, and a control .inv file shows
// the watcher is live and that earlier, ignored changes have already been delivered (or dropped).
public sealed class FileSystemServiceWatcherTests : IDisposable
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly FileSystemService _service;
    private readonly Channel<FileSystemEventArgs> _events = Channel.CreateUnbounded<FileSystemEventArgs>();
    private int _control;

    public FileSystemServiceWatcherTests()
    {
        // Canonical path (/private/var on macOS): the watcher reports canonical paths.
        var created = Directory.CreateTempSubdirectory("Invoklet-watcher-test").FullName;
        _root = RealPath(created);
        _service = new FileSystemService(NullLogger<FileSystemService>.Instance, _root);
        _service.WatcherEvent += (_, e) => _events.Writer.TryWrite(e);
        _service.StartWatcher();
    }

    // /var/folders/… is a symlink to /private/var/folders/… on macOS, and events name the real path.
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in parts.Skip(OperatingSystem.IsWindows() ? 1 : 0))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).LinkTarget is { } target)
                current = Path.GetFullPath(target, Path.GetDirectoryName(current)!);
        }
        return current;
    }

    public void Dispose()
    {
        _service.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    // Creates a control script and returns the events delivered before it (its own included last).
    private async Task<List<FileSystemEventArgs>> EventsUntilControlAsync()
    {
        var name = $"control{++_control}.inv";
        File.WriteAllText(P(name), "");
        var seen = new List<FileSystemEventArgs>();
        using var cts = new CancellationTokenSource(EventTimeout);
        while (true)
        {
            var e = await _events.Reader.ReadAsync(cts.Token);
            seen.Add(e);
            if (Path.GetFileName(e.FullPath) == name) return seen;
        }
    }

    private static bool Mentions(FileSystemEventArgs e, string path) =>
        PathEquals(e.FullPath, path) || (e is RenamedEventArgs r && PathEquals(r.OldFullPath, path));

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);

    private async Task<FileSystemEventArgs> WaitForAsync(string path)
    {
        using var cts = new CancellationTokenSource(EventTimeout);
        while (true)
        {
            var e = await _events.Reader.ReadAsync(cts.Token);
            if (Mentions(e, path)) return e;
        }
    }

    [Fact]
    public async Task ADottedFolder_CreatedAndRenamedOutsideInvoklet_Should_RaiseEvents()
    {
        await EventsUntilControlAsync();

        Directory.CreateDirectory(P("project.v1"));
        await WaitForAsync(P("project.v1"));

        Directory.Move(P("project.v1"), P("project.v2"));
        await WaitForAsync(P("project.v2"));
    }

    [Fact]
    public async Task AFolderWithScripts_MovedIn_AndDeleted_Should_RaiseEvents()
    {
        await EventsUntilControlAsync();
        var outside = Directory.CreateTempSubdirectory("Invoklet-watcher-outside").FullName;
        Directory.CreateDirectory(Path.Combine(outside, "Imported"));
        File.WriteAllText(Path.Combine(outside, "Imported", "a.inv"), "");

        Directory.Move(Path.Combine(outside, "Imported"), P("Imported"));
        await WaitForAsync(P("Imported"));
        Directory.Delete(outside);

        // The tree walk records the folder, so its deletion is recognised as a folder's.
        await _service.GetFilesTreeAsync(TestContext.Current.CancellationToken);
        Directory.Delete(P("Imported"), recursive: true);
        await WaitForAsync(P("Imported"));
    }

    // Hiding or showing a folder by renaming it to or from a dot name changes the tree both ways.
    [Fact]
    public async Task RenamingAFolderToAndFromADotName_Should_RaiseEvents()
    {
        Directory.CreateDirectory(P("Drafts"));
        await _service.GetFilesTreeAsync(TestContext.Current.CancellationToken);
        await EventsUntilControlAsync();

        Directory.Move(P("Drafts"), P(".Drafts"));
        await WaitForAsync(P("Drafts"));

        Directory.Move(P(".Drafts"), P("Drafts"));
        await WaitForAsync(P("Drafts"));
    }

    // Changes that can't change the tree: plain text files, anything inside a dot folder.
    [Fact]
    public async Task UnrelatedFilesAndDotFolderContents_Should_NotRaiseEvents()
    {
        Directory.CreateDirectory(P(".obsidian"));
        Directory.CreateDirectory(P("Notes"));
        await EventsUntilControlAsync();

        File.WriteAllText(P("notes.md"), "x");
        File.WriteAllText(P("Notes", "todo.txt"), "x");
        File.WriteAllText(P(".obsidian", "workspace.json"), "{}");
        Directory.CreateDirectory(P(".obsidian", "plugins", "x"));
        File.WriteAllText(P(".obsidian", "plugins", "x", "inside.inv"), "");

        var seen = await EventsUntilControlAsync();

        // Control scripts (their own Created/Changed may arrive late) are the only events.
        Assert.All(seen, e => Assert.StartsWith("control", Path.GetFileName(e.FullPath)));
    }

    // Scripts keep notifying, a dot-named script included (it is listed).
    [Fact]
    public async Task Scripts_Should_StillRaiseEvents()
    {
        Directory.CreateDirectory(P("Notes"));
        await EventsUntilControlAsync();

        File.WriteAllText(P("Notes", "new.inv"), "");
        await WaitForAsync(P("Notes", "new.inv"));
        File.WriteAllText(P(".dotted.inv"), "");
        await WaitForAsync(P(".dotted.inv"));
    }
}
