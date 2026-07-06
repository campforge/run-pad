using System.Collections.ObjectModel;
using ReactiveUI;

namespace DotPad.Assets.Models;

public class Node : ReactiveObject
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

    public ObservableCollection<Node>? SubNodes { get; }

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

    public Node(string title, string filePath)
    {
        _title = title;
        FilePath = filePath;
    }

    public Node(string title, string filePath, ObservableCollection<Node> subNodes)
    {
        _title = title;
        FilePath = filePath;
        SubNodes = subNodes;
    }
}
