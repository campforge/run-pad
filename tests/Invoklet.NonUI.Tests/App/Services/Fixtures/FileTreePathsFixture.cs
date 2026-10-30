using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

// FileTreePaths has no injected dependencies besides the root folder and the comparison the platform uses for paths.
public sealed class FileTreePathsFixture
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "tree-root");

    public static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    internal FileTreePaths CreateSut(StringComparison comparison = StringComparison.Ordinal) => new(Root, comparison);
}
