using Invoklet.Format.RPad;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Save As onto another Windows volume: no relative path reaches the file, so the absolute one is
// written (with '/'), also in place of $(CurrentQueryPath). Windows path rules apply only on Windows,
// so elsewhere this is reported as skipped. Lexical only — no drive is touched.
public sealed class ReferencePathWindowsVolumeTests
{
    [Theory]
    [InlineData("lib/A.dll")]
    [InlineData("lib\\A.dll")]
    [InlineData("$(CurrentQueryPath)/lib/A.dll")]
    public void Rebase_OntoAnotherWindowsVolume_Should_WriteTheAbsolutePath(string reference)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows volume rules apply only on Windows.");

        Assert.Equal("C:/Scripts/Old/lib/A.dll", ReferencePath.Rebase(reference, "C:\\Scripts\\Old", "D:\\Elsewhere", windowsHost: true));
    }
}
