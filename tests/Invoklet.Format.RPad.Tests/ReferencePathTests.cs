using Xunit;

namespace Invoklet.Format.RPad.Tests;

// Reference spelling, judged from the text alone and the same on every host: Windows drive and
// network paths are recognised in either spelling and written with '/'; on a Unix host a backslash
// anywhere else is a file-name character and stays.
public sealed class ReferencePathTests
{
    [Theory]
    [InlineData("C:\\Lib\\A.dll", ReferencePathKind.WindowsDrive)]
    [InlineData("C:/Lib/A.dll", ReferencePathKind.WindowsDrive)]
    [InlineData("d:\\A.dll", ReferencePathKind.WindowsDrive)]
    [InlineData("\\\\server\\share\\A.dll", ReferencePathKind.WindowsUnc)]
    [InlineData("//server/share/A.dll", ReferencePathKind.WindowsUnc)]
    [InlineData("\\\\server/share\\A.dll", ReferencePathKind.WindowsUnc)]
    [InlineData("C:lib/File.dll", ReferencePathKind.WindowsDriveRelative)]
    [InlineData("C:", ReferencePathKind.WindowsDriveRelative)]
    [InlineData("/opt/lib/A.dll", ReferencePathKind.Rooted)]
    [InlineData("lib/A.dll", ReferencePathKind.Relative)]
    [InlineData("lib\\A.dll", ReferencePathKind.Relative)]
    [InlineData("../A.dll", ReferencePathKind.Relative)]
    [InlineData("$(MyDocuments)/A.dll", ReferencePathKind.Relative)]
    public void Classify_Should_JudgeTheSpellingAlone(string reference, ReferencePathKind expected)
    {
        Assert.Equal(expected, ReferencePath.Classify(reference));
    }

    [Theory]
    // Windows drive and network paths: '/' on any host, root and destination kept.
    [InlineData("C:\\Lib\\A.dll", false, "C:/Lib/A.dll")]
    [InlineData("C:\\Lib\\A.dll", true, "C:/Lib/A.dll")]
    [InlineData("\\\\server\\share\\Lib\\A.dll", false, "//server/share/Lib/A.dll")]
    [InlineData("\\\\server/share\\A.dll", false, "//server/share/A.dll")]
    // Unix host: a backslash elsewhere is part of a file name.
    [InlineData("lib\\A.dll", false, "lib\\A.dll")]
    [InlineData("/opt/odd\\name.dll", false, "/opt/odd\\name.dll")]
    [InlineData("$(MyDocuments)\\Shared\\A.dll", false, "$(MyDocuments)\\Shared\\A.dll")]
    // Windows host: '\' can only be a separator.
    [InlineData("lib\\A.dll", true, "lib/A.dll")]
    [InlineData("$(MyDocuments)\\Shared\\A.dll", true, "$(MyDocuments)/Shared/A.dll")]
    [InlineData("..\\Lib\\A.dll", true, "../Lib/A.dll")]
    // Nothing but separators changes.
    [InlineData(" Spaced Dir/It's 日本語 🌍.DLL ", false, " Spaced Dir/It's 日本語 🌍.DLL ")]
    [InlineData("C:\\Mixed Case\\It's.DLL", false, "C:/Mixed Case/It's.DLL")]
    [InlineData("C:lib\\File.dll", false, "C:lib\\File.dll")]
    public void Canonical_Should_OnlyChangeSeparators_WhereTheyAreUnambiguous(string reference, bool windowsHost, string expected)
    {
        Assert.Equal(expected, ReferencePath.Canonical(reference, windowsHost));
        Assert.Equal(expected, ReferencePath.Canonical(expected, windowsHost)); // stable
    }

    [Theory]
    [InlineData("C:\\Lib\\A.dll", false, "C:/Lib/A.dll")]
    [InlineData("\\\\server\\share\\A.dll", false, "//server/share/A.dll")]
    [InlineData("lib\\A.dll", false, "lib\\A.dll")]
    [InlineData("lib\\A.dll", true, "lib/A.dll")]
    [InlineData("It's/日本語 dir/🌍.dll", false, "It's/日本語 dir/🌍.dll")]
    public void Writing_Should_SaveTheCanonicalSpelling_AndStayStableAcrossSaves(string reference, bool windowsPaths, string expected)
    {
        var parsed = RpadParser.Parse("---\nlanguage: csharp\nreferences:\n  - " + System.Text.Json.JsonSerializer.Serialize(reference) + "\n---\n```csharp run\n1.Dump();\n```\n");
        Assert.True(parsed.IsSuccess, parsed.Error);
        var document = parsed.Document!;

        var first = RpadWriter.Write(document, windowsPaths);
        var reread = RpadParser.Parse(first).Document!;
        var second = RpadWriter.Write(reread, windowsPaths);

        Assert.Equal(expected, Assert.Single(reread.FrontMatter.References!));
        Assert.Equal(first, second);
        Assert.Equal(reference, Assert.Single(document.FrontMatter.References!)); // the document itself is not rewritten
    }

    // ── Save As into another folder ──────────────────────────────────────────

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Invoklet-rebase");
    private static readonly string Old = Path.Combine(Root, "scripts", "old");
    private static readonly string New = Path.Combine(Root, "elsewhere");

    [Theory]
    [InlineData("lib/A.dll", "../scripts/old/lib/A.dll")]
    [InlineData("../shared/A.dll", "../scripts/shared/A.dll")]
    [InlineData("$(CurrentQueryPath)/lib/A.dll", "$(CurrentQueryPath)/../scripts/old/lib/A.dll")]
    public void Rebase_Should_ReachTheSameFile_FromTheNewFolder(string reference, string expected)
    {
        var windows = OperatingSystem.IsWindows();
        var rebased = ReferencePath.Rebase(reference, Old, New, windows);

        Assert.Equal(expected, rebased);
        var macro = ReferencePath.CurrentQueryPathMacro;
        string Target(string r, string dir) => Path.GetFullPath(Path.Combine(dir, r.StartsWith(macro) ? r[(macro.Length + 1)..] : r));
        Assert.Equal(Target(reference, Old), Target(rebased, New));
    }

    [Theory]
    [InlineData("/opt/lib/A.dll")]
    [InlineData("C:/Lib/A.dll")]
    [InlineData("//server/share/A.dll")]
    [InlineData("$(MyDocuments)/A.dll")]
    [InlineData("$(RuntimeDirectory)/A.dll")]
    public void Rebase_Should_LeavePathsThatDoNotDependOnTheScriptsFolder(string reference)
    {
        Assert.Equal(reference, ReferencePath.Rebase(reference, Old, New, OperatingSystem.IsWindows()));
    }

    // The same folder: nothing is rewritten, the user's own spelling included.
    [Theory]
    [InlineData("./lib/../lib/A.dll")]
    [InlineData("lib\\A.dll")]
    public void Rebase_Should_KeepTheSpelling_WhenTheFolderStaysTheSame(string reference)
    {
        Assert.Equal(reference, ReferencePath.Rebase(reference, Old, Old + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()));
    }

    // On a Unix host a backslash in a relative reference is part of a file name: it moves with it.
    [Fact]
    public void Rebase_OnUnix_Should_KeepALiteralBackslash_InTheFileName()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("../scripts/old/odd\\name.dll", ReferencePath.Rebase("odd\\name.dll", Old, New, windowsHost: false));
    }
}
