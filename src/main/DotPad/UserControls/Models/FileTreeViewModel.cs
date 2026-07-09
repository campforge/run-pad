using System.Collections.ObjectModel;
using DotPad.Services;
using ReactiveUI;

namespace DotPad.UserControls.Models;

public class FileTreeViewNode : ReactiveObject
{
    private string _title;
    private bool _isRenaming;
    private string _editingName = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            this.RaiseAndSetIfChanged(ref _title, value);
            this.RaisePropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName => SubNodes is null
        ? Path.GetFileNameWithoutExtension(Title)
        : Title;

    public string FilePath { get; set; }

    public ObservableCollection<FileTreeViewNode>? SubNodes { get; }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => this.RaiseAndSetIfChanged(ref _isRenaming, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => this.RaiseAndSetIfChanged(ref _editingName, value);
    }

    public FileTreeViewNode(string title, string filePath)
    {
        _title = title;
        FilePath = filePath;
    }

    public FileTreeViewNode(string title, string filePath, ObservableCollection<FileTreeViewNode> subNodes)
    {
        _title = title;
        FilePath = filePath;
        SubNodes = subNodes;
    }
}

public class FileTreeViewModel
{
    public FileTreeViewModel()
    {
        Nodes = [];
    }

    public ObservableCollection<FileTreeViewNode> Nodes { get; }

    /// <summary>Rebuilds <see cref="Nodes"/> from disk, preserving existing node
    /// objects where possible so selection and expand state survive small changes.</summary>
    public void Refresh(IReadOnlyList<FileSystemItem> tree) => MergeNodeList(Nodes, tree);

    public void RemoveByPath(string filePath) => RemoveNodeByPath(Nodes, filePath);

    private static FileTreeViewNode MapToNode(FileSystemItem fsi) => fsi.IsDirectory
        ? new FileTreeViewNode(fsi.Name, fsi.FilePath, new ObservableCollection<FileTreeViewNode>((fsi.Children ?? []).Select(MapToNode)))
        : new FileTreeViewNode(fsi.Name, fsi.FilePath);

    /// <summary>Merges <paramref name="fresh"/> into <paramref name="current"/> with minimal
    /// ObservableCollection mutations to preserve UI state (selection, expanded folders).</summary>
    private static void MergeNodeList(ObservableCollection<FileTreeViewNode> current, IReadOnlyList<FileSystemItem> fresh)
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
}
