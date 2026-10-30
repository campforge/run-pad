using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class FileTreeOperationsFixture
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "ops-root");

    public Mock<IDiskOperations> Disk { get; } = new();

    public Mock<IWindowService> Windows { get; } = new();

    public Mock<IFileOperationTabs> Tabs { get; } = new();

    public Mock<IFileTreeSurface> Tree { get; } = new();

    public List<string> Calls { get; } = [];

    public static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public FileTreeOperationsFixture()
    {
        Windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        Tabs.SetupGet(t => t.OpenPaths).Returns([]);
        SaveSucceeds(true);
        Disk.Setup(d => d.Delete(It.IsAny<string>(), It.IsAny<bool>())).Callback(() => Calls.Add("disk"));
        Disk.Setup(d => d.Move(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Callback(() => Calls.Add("disk"));
    }

    public void OpenTabs(params string[] paths) => Tabs.SetupGet(t => t.OpenPaths).Returns(paths);

    public void SaveSucceeds(bool succeeds)
        => Tabs.Setup(t => t.SaveAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>()))
            .Callback(() => Calls.Add("save")).ReturnsAsync(succeeds);

    public void FolderExists(string path) => Disk.Setup(d => d.IsDirectory(path)).Returns(true);

    public FileTreeOperations CreateSut()
        => new(Disk.Object, Windows.Object, Tabs.Object, Tree.Object, new FileTreePaths(Root, StringComparison.Ordinal));
}
