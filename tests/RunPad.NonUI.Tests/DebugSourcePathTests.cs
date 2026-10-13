using RunPad.Debug;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Breakpoints stayed pending on macOS because they were sent
// for /var/folders/... while the PDB recorded /private/var/folders/... (/var is a symlink). The
// symlink is an ANCESTOR of the source file, not the file itself, so resolving only the leaf
// (FileInfo.ResolveLinkTarget) isn't enough.
public class DebugSourcePathTests : IDisposable
{
    private readonly string _root;

    public DebugSourcePathTests()
    {
        // /tmp, not Path.GetTempPath(): on macOS /tmp is itself a symlink (to /private/tmp), so every
        // test here also crosses a system-level ancestor link like the real /var one.
        _root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "runpad-srcpath-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (string RealFile, string AliasFile) CreateFileBehindAncestorLink(string linkTarget)
    {
        var realDir = Directory.CreateDirectory(Path.Combine(_root, "real", "out")).FullName;
        var realFile = Path.Combine(realDir, "RunPadQuery.cs");
        File.WriteAllText(realFile, "int x = 1;");
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), linkTarget);
        return (realFile, Path.Combine(_root, "alias", "out", "RunPadQuery.cs"));
    }

    [Fact]
    public void Canonicalize_Should_ResolveASymlinkedAncestorDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (realFile, aliasFile) = CreateFileBehindAncestorLink(Path.Combine(_root, "real"));

        var canonical = DebugSourcePath.Canonicalize(aliasFile);

        Assert.Equal(DebugSourcePath.Canonicalize(realFile), canonical);
        Assert.DoesNotContain("alias", canonical);
        Assert.True(File.Exists(canonical));
    }

    [Fact]
    public void Canonicalize_Should_ResolveARelativeAncestorLinkTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (realFile, aliasFile) = CreateFileBehindAncestorLink("real");

        Assert.Equal(DebugSourcePath.Canonicalize(realFile), DebugSourcePath.Canonicalize(aliasFile));
    }

    [Fact]
    public void Canonicalize_Should_ResolveTheSystemTmpAlias_OnMacOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "/tmp → /private/tmp is a macOS layout.");

        var canonical = DebugSourcePath.Canonicalize(Path.Combine(_root, "missing.cs"));

        Assert.StartsWith("/private/tmp/", canonical);
        Assert.EndsWith("missing.cs", canonical);
    }

    [Fact]
    public void Canonicalize_Should_KeepANonExistentLeaf_AfterResolvingItsAncestors()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (_, aliasFile) = CreateFileBehindAncestorLink(Path.Combine(_root, "real"));
        var missing = Path.Combine(Path.GetDirectoryName(aliasFile)!, "Other.cs");

        var canonical = DebugSourcePath.Canonicalize(missing);

        Assert.Equal(Path.Combine(DebugSourcePath.Canonicalize(Path.Combine(_root, "real", "out")), "Other.cs"), canonical);
    }

    [Fact]
    public void Canonicalize_Should_ReturnTheInput_ForASymlinkCycle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);
        var path = Path.Combine(a, "x.cs");

        Assert.Equal(path, DebugSourcePath.Canonicalize(path));
    }

    [Fact]
    public void AreSame_Should_MatchAnAliasAgainstTheCanonicalPath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (realFile, aliasFile) = CreateFileBehindAncestorLink(Path.Combine(_root, "real"));

        Assert.True(DebugSourcePath.AreSame(aliasFile, DebugSourcePath.Canonicalize(realFile)));
        Assert.False(DebugSourcePath.AreSame(aliasFile, Path.Combine(_root, "real", "out", "Other.cs")));
    }

    [Fact]
    public void Canonicalize_Should_LeaveRelativeAndEmptyPathsUnchanged()
    {
        Assert.Equal("", DebugSourcePath.Canonicalize(""));
        Assert.Equal("RunPadQuery.cs", DebugSourcePath.Canonicalize("RunPadQuery.cs"));
    }
}
