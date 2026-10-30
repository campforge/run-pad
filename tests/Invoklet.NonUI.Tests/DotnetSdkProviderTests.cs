using Invoklet.Languages.DotnetCommon.Sdk;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Live report 2026-07-26 — the first-run setup wizard let a user browse for a custom dotnet path, but
// DotnetSdkProvider ignored it entirely (always shelled the bare "dotnet" command). Fixed to accept an
// override; these tests exercise the override with a REAL child process (a tiny fake "dotnet"
// script/batch file), not a mock, since the actual bug this guards against — Process.Start behavior
// for a path containing spaces — can only be proven by really spawning one.
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public class DotnetSdkProviderTests
{
    [Fact]
    public void Get_Should_ListRealInstalledSdks_WhenNoOverrideIsGiven()
    {
        // No override — resolves "dotnet" via PATH, same as before this override existed. This
        // machine has a real .NET SDK installed (it's what's building/running this very test).
        var result = new DotnetSdkProvider().Get();

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Get_Should_UseTheOverridePath_InsteadOfPath()
    {
        var dir = Directory.CreateTempSubdirectory("Invoklet-sdkprovider-test");
        try
        {
            var fakeDotnet = CreateFakeDotnetListSdks(dir.FullName, "9.0.100 [/fake/sdk]\n6.0.412 [/fake/sdk]");

            var result = new DotnetSdkProvider().Get(fakeDotnet);

            Assert.Contains(result, sdk => sdk.RuntimeConfigVersion == "9.0.0");
            Assert.Contains(result, sdk => sdk.RuntimeConfigVersion == "6.0.0");
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    // The real regression this exists to catch: DotnetSdkProvider used to run everything through
    // Utils.Shell, whose SplitArguments naively splits on every space with no quoting support —
    // exactly what a real, common install path (Windows' own default "C:\Program Files\dotnet\...")
    // would hit. Now runs via Process.Start directly instead, which handles this correctly.
    [Fact]
    public void Get_Should_UseTheOverridePath_EvenWhenItContainsASpace()
    {
        var dir = Directory.CreateTempSubdirectory("Invoklet-sdkprovider-test with a space");
        try
        {
            var fakeDotnet = CreateFakeDotnetListSdks(dir.FullName, "8.0.100 [/fake/sdk]");

            var result = new DotnetSdkProvider().Get(fakeDotnet);

            Assert.Contains(result, sdk => sdk.RuntimeConfigVersion == "8.0.0");
        }
        finally { Directory.Delete(dir.FullName, recursive: true); }
    }

    [Fact]
    public void Get_Should_Throw_WhenTheOverridePathDoesNotExist()
    {
        var provider = new DotnetSdkProvider();

        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(
            () => provider.Get(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-executable-xyz")));
    }

    // Builds a tiny real, executable "fake dotnet" that just prints a canned `dotnet --list-sdks`
    // response — a .cmd batch file on Windows (Process.Start needs a real executable, not a shell
    // script interpreted by something else), a shebang bash script elsewhere.
    private static string CreateFakeDotnetListSdks(string directory, string output)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, "fake-dotnet.cmd");
            File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", output.Split('\n').Select(l => $"echo {l}")) + "\r\n");
            return path;
        }
        else
        {
            var path = Path.Combine(directory, "fake-dotnet.sh");
            File.WriteAllText(path, "#!/bin/sh\n" + string.Join("\n", output.Split('\n').Select(l => $"echo '{l}'")) + "\n");
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return path;
        }
    }
}
