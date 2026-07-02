using System.Collections.ObjectModel;

namespace DotPad.Assets.Models;

public class Node
{
    public string Title { get; }
    public string FilePath { get; }
    public ObservableCollection<Node>? SubNodes { get; }

    public Node(string title, string filePath)
    {
        Title = title;
        FilePath = filePath;
    }

    public Node(string title, string filePath, ObservableCollection<Node> subNodes)
    {
        Title = title;
        FilePath = filePath;
        SubNodes = subNodes;
    }
}
