using System.Collections.ObjectModel;
using DotPad.Contracts;
using DotPad.Format.RPad;
using DotPad.Services;
using DotPad.Services.Preferences;

namespace DotPad.UserControls.Models;

public class FileTreeViewModel
{
    private readonly IPreferencesService _prefs;
    private readonly ILanguageHostRegistry _languages;

    public FileTreeViewModel(IPreferencesService prefs, ILanguageHostRegistry languages)
    {
        _prefs = prefs;
        _languages = languages;
        Nodes = [];
    }

    public ObservableCollection<FileTreeViewNode> Nodes { get; }

    // TODO.md #80: FileTreeView's F2/Delete handling used to check literal Key.F2/Key.Delete directly
    // — FileTreeView itself has no DI (it's instantiated by a plain XAML DataTemplate, not resolved
    // through the service container, so it can't take IPreferencesService as a constructor
    // parameter) — routed through this view model (its DataContext) instead, read live on every
    // keypress the same way MainWindow.OnWindowKeyDown re-reads _prefs.Keyboard fresh each time,
    // rather than a value snapshotted once at construction.
    public string RenameGesture => _prefs.Keyboard.RenameFile;
    public string DeleteGesture => _prefs.Keyboard.DeleteFile;

    /// <summary>Rebuilds <see cref="Nodes"/> from disk, preserving existing node
    /// objects where possible so selection and expand state survive small changes.</summary>
    public void Refresh(IReadOnlyList<FileSystemItem> tree) => MergeNodeList(Nodes, tree);

    public void RemoveByPath(string filePath) => RemoveNodeByPath(Nodes, filePath);

    /// <summary>Finds the node for <paramref name="filePath"/>, if it's currently in the tree —
    /// used right after creating a new folder (TODO.md #69) to enter rename mode on it immediately.</summary>
    public FileTreeViewNode? FindByPath(string filePath) => FindNodeByPath(Nodes, filePath);

    // Language is peeked once here (TODO.md #70) via the same cheap front-matter parse
    // EditorTabsControl already uses to pick an IntelliSense factory — not re-peeked on every later
    // refresh for nodes MergeNodeList preserves, only when a node is freshly built (initial load, or
    // a path that's new/renamed/moved). No longer static (TODO.md #81) — needs _languages to resolve
    // the node's icon key off the owning ILanguageHostFactory instead of switching on RpadLanguage.
    private FileTreeViewNode MapToNode(FileSystemItem fsi) => fsi.IsDirectory
        ? new FileTreeViewNode(fsi.Name, fsi.FilePath, new ObservableCollection<FileTreeViewNode>((fsi.Children ?? []).Select(MapToNode)))
        : MapFileNode(fsi);

    private FileTreeViewNode MapFileNode(FileSystemItem fsi)
    {
        var language = RpadFilePeek.PeekLanguage(fsi.FilePath);
        var iconKey = language is { } lang && _languages.IsRegistered(lang.ToLanguageId())
            ? _languages.GetEditorProfile(lang.ToLanguageId()).IconResourceKey
            : null;
        return new FileTreeViewNode(fsi.Name, fsi.FilePath, language) { LanguageIconKey = iconKey };
    }

    /// <summary>Merges <paramref name="fresh"/> into <paramref name="current"/> with minimal
    /// ObservableCollection mutations to preserve UI state (selection, expanded folders).</summary>
    private void MergeNodeList(ObservableCollection<FileTreeViewNode> current, IReadOnlyList<FileSystemItem> fresh)
    {
        // Build a lookup of fresh paths for O(1) access
        var freshByPath = fresh.ToDictionary(n => n.FilePath, StringComparer.OrdinalIgnoreCase);

        // 1. Remove nodes that no longer exist on disk
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!freshByPath.ContainsKey(current[i].FilePath))
                current.RemoveAt(i);
        }

        // 2. For surviving folder nodes, merge their children recursively
        foreach (var existing in current)
        {
            if (existing.SubNodes is not null && freshByPath.TryGetValue(existing.FilePath, out var freshFolder))
                MergeNodeList(existing.SubNodes, freshFolder.Children ?? []);
        }

        // 3. Add nodes that are missing from current (new files/folders)
        var currentPaths = new HashSet<string>(
            current.Select(n => n.FilePath), StringComparer.OrdinalIgnoreCase);

        int insertIndex = 0;

        foreach (var freshNode in fresh)
        {
            if (!currentPaths.Contains(freshNode.FilePath))
                current.Insert(Math.Min(insertIndex, current.Count), MapToNode(freshNode));
            insertIndex++;
        }
    }

    private static bool RemoveNodeByPath(ObservableCollection<FileTreeViewNode> nodes, string filePath)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                nodes.RemoveAt(i);
                return true;
            }

            if (nodes[i].SubNodes is { } sub && RemoveNodeByPath(sub, filePath))
                return true;
        }

        return false;
    }

    private static FileTreeViewNode? FindNodeByPath(ObservableCollection<FileTreeViewNode> nodes, string filePath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var node in nodes)
        {
            if (string.Equals(node.FilePath, filePath, comparison))
                return node;
            if (node.SubNodes is { } sub && FindNodeByPath(sub, filePath) is { } found)
                return found;
        }

        return null;
    }
}
