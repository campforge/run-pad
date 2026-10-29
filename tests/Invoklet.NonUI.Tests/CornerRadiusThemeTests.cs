using System.Text.RegularExpressions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Every corner radius comes from the theme's metrics (Invoklet.Radius.* in
// src/controls/Invoklet.Avalonia.Themes/Metrics.axaml), so windows, dialogs, popups and controls stay
// alike and are changed in one place. Square corners (0) and shapes whose radius follows their own
// size are the exceptions.
public class CornerRadiusThemeTests
{
    private static readonly string[] Roots =
    [
        Path.Combine("src", "main", "Invoklet"),
        Path.Combine("src", "controls", "Invoklet.Avalonia.Themes"),
        Path.Combine("src", "controls", "Invoklet.Avalonia.CodeTextEditor"),
    ];

    // A circle: half of its 24 px size.
    private static readonly string[] Allowed = ["DialogLayout.axaml: CornerRadius=\"12\""];

    private static readonly Regex Literal = new(
        @"CornerRadius=""(?<v>[0-9][^""]*)""|Property=""CornerRadius""\s+Value=""(?<v>[0-9][^""]*)""|new CornerRadius\((?<v>[0-9][^)]*)\)",
        RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Invoklet.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Invoklet.sln) from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void CornerRadii_Should_ComeFromTheThemesMetrics()
    {
        var root = RepoRoot();
        var found = Roots
            .SelectMany(r => Directory.EnumerateFiles(Path.Combine(root, r), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "Metrics.axaml")
            .SelectMany(f => Literal.Matches(File.ReadAllText(f))
                .Where(m => m.Groups["v"].Value.Split(',').Any(part => double.Parse(part.Trim()) != 0))
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .Except(Allowed)
            .ToList();

        Assert.True(found.Count == 0, "Hard-coded corner radii: " + string.Join("; ", found));
    }
}
