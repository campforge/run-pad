using System.Collections.ObjectModel;
using DotPad.Format.RPad;
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

    public bool IsFolder => SubNodes is not null;

    // Peeked once from the .rpad front matter when this node is built (FileTreeViewModel.MapToNode)
    // — null for folders. Used by the tree's per-language icon (TODO.md #70); a file's language
    // re-peeks the next time its node is rebuilt (rename/move creates a fresh node — see
    // FileTreeViewModel.MergeNodeList), not on every unrelated tree refresh, matching how DisplayName
    // itself is only ever set once from the same peek.
    public RpadLanguage? Language { get; init; }

    // Resource key (Resources/Icons.axaml) of the Material Design icon for this file's language, if
    // one exists — null falls back to the tree's generic file icon (FileTreeView.axaml resolves the
    // key to an actual Geometry via ResourceKeyToGeometryConverter). TODO.md #81: used to be a
    // `switch` on RpadLanguage hardcoded right here in the main UI project — now supplied by
    // FileTreeViewModel.MapToNode, which reads it off the owning ILanguageHostFactory's
    // EditorLanguageProfile.IconResourceKey (same seam TODO #76 used for FoldingRegionsProvider), so
    // this class no longer needs to know any language's identity to pick its own icon.
    public string? LanguageIconKey { get; init; }

    // The tree's fallback "plain document" icon — shown for a file whose language has no entry
    // above (any future RpadLanguage not yet given an icon) instead of leaving nothing rendered.
    public bool ShowGenericFileIcon => !IsFolder && LanguageIconKey is null;

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

    // TODO.md #110 — live user request: the OS drag cursor alone wasn't a reliable/visible enough
    // signal of where a drag-and-drop would actually land, so the tree highlights the current drop
    // target itself too (FileTreeView's drag handlers toggle this on whatever node is under the
    // pointer during a valid drag).
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => this.RaiseAndSetIfChanged(ref _isDropTarget, value);
    }

    public FileTreeViewNode(string title, string filePath, RpadLanguage? language = null)
    {
        _title = title;
        FilePath = filePath;
        Language = language;
    }

    public FileTreeViewNode(string title, string filePath, ObservableCollection<FileTreeViewNode> subNodes)
    {
        _title = title;
        FilePath = filePath;
        SubNodes = subNodes;
    }
}
