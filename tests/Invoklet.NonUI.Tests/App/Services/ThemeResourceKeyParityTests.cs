using System.Xml.Linq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

// Audit finding: the 8 theme files (src/controls/Invoklet.Avalonia.Themes/Themes/*.axaml)
// duplicate the same set of x:Key="Invoklet.*" SolidColorBrush entries, differing only in Color values.
// That key list isn't recorded anywhere as an actual "schema" — a key added to one theme and
// forgotten in another doesn't fail to compile, it just leaves that theme's element unstyled at
// runtime. Direction from the TODO itself: don't eliminate the duplication (inherent to a theming
// system built this way) — add a test that catches the sets drifting apart.
//
// Reads the raw source files off disk rather than via Avalonia's AssetLoader (avares://) — compiled
// AXAML Styles files aren't retrievable as a raw byte stream that way (confirmed empirically:
// AssetLoader.Open threw FileNotFoundException for avares://Invoklet.Avalonia.Themes/Themes/....axaml
// even though the exact same URI works fine as a StyleInclude Source, a completely different loading
// path) — so this walks up from the test assembly's own output directory to find the repo root
// (marked by Invoklet.sln) instead, then reads the actual shipped source directly.
public class ThemeResourceKeyParityTests
{
    private static readonly string[] ThemeFiles =
    [
        "VsCodeDark", "VsCodeLight",
        "RiderDark", "RiderLight",
        "Vs2022Dark", "Vs2022Light",
        "InvokletMidnight", "InvokletWarmLight",
    ];

    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Invoklet.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Invoklet.sln) from " + AppContext.BaseDirectory);
    }

    private static HashSet<string> LoadInvokletKeys(string themeName)
    {
        var path = Path.Combine(RepoRoot(), "src", "controls", "Invoklet.Avalonia.Themes", "Themes", themeName + ".axaml");
        var doc = XDocument.Load(path);
        return doc.Descendants()
            .Select(e => (string?)e.Attribute(XNs + "Key"))
            .Where(k => k is not null && k.StartsWith("Invoklet.", StringComparison.Ordinal))
            .Select(k => k!)
            .ToHashSet();
    }

    [Fact]
    public void AllThemeFiles_Should_DefineTheExactSameSetOfInvokletResourceKeys()
    {
        var keysByTheme = ThemeFiles.ToDictionary(t => t, LoadInvokletKeys);
        var reference = keysByTheme[ThemeFiles[0]];

        foreach (var (theme, keys) in keysByTheme)
        {
            var missing = reference.Except(keys).ToList();
            var extra = keys.Except(reference).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"{theme} differs from {ThemeFiles[0]}: missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]");
        }
    }
}
