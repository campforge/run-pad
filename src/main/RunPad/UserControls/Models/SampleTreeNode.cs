using System.Collections.ObjectModel;
using ReactiveUI;
using RunPad.Format.RPad;
using RunPad.Services.Examples;

namespace RunPad.UserControls.Models;

/// <summary>A row of the sidebar's Samples tree: a folder of samples, or a sample.</summary>
public sealed class SampleTreeNode : ReactiveObject
{
    private SampleTreeNode(string id, string title, ExampleScript? example)
    {
        Id = id;
        Title = title;
        Example = example;
        if (example is null) Children = [];
    }

    /// <summary>A folder's path ("getting-started"), or a sample's <see cref="ExampleScript.Id"/>.</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Null for a folder.</summary>
    public ExampleScript? Example { get; }

    /// <summary>Null for a sample.</summary>
    public ObservableCollection<SampleTreeNode>? Children { get; }

    public bool IsFolder => Example is null;

    /// <summary>The sample's language icon (Resources/Icons.axaml); null for a folder.</summary>
    public string? IconKey => Example?.Language switch
    {
        null => null,
        RpadLanguage.Vb => "Icon.LanguageVb",
        _ => "Icon.LanguageCSharp",
    };

    public string AccessibleName => IsFolder
        ? $"{Title} folder, {Children!.Count} {(Children.Count == 1 ? "item" : "items")}"
        : $"{Title} sample";

    public bool IsExpanded
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ExpandedChanged?.Invoke(this);
        }
    }

    internal event Action<SampleTreeNode>? ExpandedChanged;

    /// <summary>
    /// The samples as a tree in their order: a folder appears where its first sample does, and its
    /// samples keep their order inside it. Folders named in <paramref name="collapsedFolders"/> start
    /// folded; <paramref name="onExpandedChanged"/> hears every later fold or unfold.
    /// </summary>
    public static IReadOnlyList<SampleTreeNode> Build(IEnumerable<ExampleScript> examples,
        IReadOnlyCollection<string>? collapsedFolders = null, Action<SampleTreeNode>? onExpandedChanged = null)
    {
        var roots = new List<SampleTreeNode>();
        var folders = new Dictionary<string, SampleTreeNode>(StringComparer.Ordinal);
        foreach (var example in examples)
        {
            var siblings = (IList<SampleTreeNode>)roots;
            var path = "";
            foreach (var segment in example.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? segment : path + "/" + segment;
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new SampleTreeNode(path, ExampleScript.FolderTitle(segment), null);
                    folder.IsExpanded = collapsedFolders?.Contains(path) != true;
                    if (onExpandedChanged is not null) folder.ExpandedChanged += onExpandedChanged;
                    folders[path] = folder;
                    siblings.Add(folder);
                }
                siblings = folder.Children!;
            }
            siblings.Add(new SampleTreeNode(example.Id, example.Title, example));
        }
        return roots;
    }

    public override string ToString() => AccessibleName;
}
