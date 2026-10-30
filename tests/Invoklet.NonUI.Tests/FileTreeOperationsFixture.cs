using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class FileTreeOperationsFixture
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "ops-root");

    private readonly Dictionary<FileOperationTab, string?> _current = [];
    private readonly List<(string From, string To)> _saveAs = [];

    public Mock<IDiskOperations> Disk { get; } = new();

    public Mock<IWindowService> Windows { get; } = new();

    public Mock<IFileOperationTabs> Tabs { get; } = new();

    public Mock<IFileTreeSurface> Tree { get; } = new();

    public List<string> Calls { get; } = [];

    public static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public FileTreeOperationsFixture()
    {
        Windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        Tabs.SetupGet(t => t.OpenTabs).Returns([]);
        Tabs.Setup(t => t.CurrentPath(It.IsAny<FileOperationTab>())).Returns((FileOperationTab tab) => _current[tab]);
        SaveSucceeds(true);
        Disk.Setup(d => d.Delete(It.IsAny<string>(), It.IsAny<bool>())).Callback(() => Calls.Add("disk"));
        Disk.Setup(d => d.Move(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback(() => Calls.Add("disk"));
    }

    /// <summary>Tabs open on these files; each is a tab of its own, on its file until a test says otherwise.</summary>
    public void OpenTabs(params string[] paths)
    {
        var tabs = paths.Select(p => new FileOperationTab(new object(), p)).ToList();
        foreach (var tab in tabs) _current[tab] = tab.Path;
        Tabs.SetupGet(t => t.OpenTabs).Returns(tabs);
    }

    /// <summary>While the tabs are saved, the user does a Save As on the tab open on <paramref name="from"/>: it is on <paramref name="to"/> after that.</summary>
    public void SaveAsMovesTab(string from, string to) => _saveAs.Add((from, to));

    public void SaveSucceeds(bool succeeds)
        => Tabs.Setup(t => t.SaveAsync(It.IsAny<IReadOnlyList<FileOperationTab>>(), It.IsAny<string>()))
            .Callback(() =>
            {
                Calls.Add("save");
                foreach (var (from, to) in _saveAs)
                    foreach (var tab in _current.Keys.Where(k => k.Path == from).ToList()) _current[tab] = to;
            }).ReturnsAsync(succeeds);

    public void ClosedMeanwhile(string path)
    {
        foreach (var tab in _current.Keys.Where(k => k.Path == path).ToList()) _current[tab] = null;
    }

    public void FolderExists(string path) => Disk.Setup(d => d.IsDirectory(path)).Returns(true);

    public static Func<IReadOnlyList<FileOperationTab>, bool> Paths(params string[] expected) => tabs => tabs.Select(t => t.Path).SequenceEqual(expected);

    public FileTreeOperations CreateSut()
        => new(Disk.Object, Windows.Object, Tabs.Object, Tree.Object, new FileTreePaths(Root, StringComparison.Ordinal));
}
