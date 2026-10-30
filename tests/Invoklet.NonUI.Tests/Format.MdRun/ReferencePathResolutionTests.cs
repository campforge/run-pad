using Invoklet.Build.Core.Models;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DiagnosticSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;

namespace Invoklet.NonUI.Tests;

// references: entries resolve against the script's own folder — never the process's current
// directory — with macros expanded at that point, and the build, the CLI's options and IntelliSense
// all get the same file. Windows-only spellings and drive-relative paths are reported, not guessed.
[Collection(RealBuildTestCollection.Name)]
public sealed class ReferencePathResolutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-reference-paths").FullName;
    private readonly string _scriptDir;
    private readonly string _script;

    public ReferencePathResolutionTests()
    {
        _scriptDir = Directory.CreateDirectory(Path.Combine(_root, "Scripts 日本語", "it's here")).FullName;
        _script = Path.Combine(_scriptDir, "query.inv");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Library(string relativeDir, string name, string code)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_scriptDir, relativeDir)).FullName;
        return ScriptCompilerTests.CompileLibrary(dir, name, code, []);
    }

    private static (string? Path, string? Error) One(string reference, string? script, bool windowsHost) =>
        ReferencePathResolver.ResolveOne(reference, script is null ? null : Path.GetFullPath(script), windowsHost);

    // ── Policy ────────────────────────────────────────────────────────────────

    [Fact]
    public void ARelativeReference_Should_ResolveAgainstTheScriptsFolder_NotTheCurrentDirectory()
    {
        var library = Library("lib", "Greeter", "public static class Greeter { }");
        Assert.NotEqual(_scriptDir, Directory.GetCurrentDirectory());

        var resolved = ReferencePathResolver.Resolve(["lib/Greeter.dll"], _script);

        Assert.Equal([Path.GetFullPath(library)], resolved.Assemblies);
        Assert.Empty(resolved.Errors);
        Assert.Empty(resolved.Warnings);
    }

    [Theory]
    [InlineData("C:\\Lib\\A.dll")]
    [InlineData("C:/Lib/A.dll")]
    [InlineData("\\\\server\\share\\A.dll")]
    [InlineData("//server/share/A.dll")]
    public void AWindowsPath_OnAnotherSystem_Should_BeReported_NeverCombinedWithTheScriptsFolder(string reference)
    {
        var (path, error) = One(reference, _script, windowsHost: false);

        Assert.Null(path);
        Assert.Contains("is a Windows path and can't be used on this system", error);
    }

    // A file literally named "C:\Lib\A.dll" next to the script must not stand in for the Windows path.
    [Fact]
    public void AWindowsPath_Should_NotResolveToALocalFileWithThatName()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_scriptDir, "C:\\Lib\\A.dll"), "");

        var resolved = ReferencePathResolver.Resolve(["C:\\Lib\\A.dll"], _script);

        Assert.Empty(resolved.Assemblies);
        Assert.Contains("is a Windows path", Assert.Single(resolved.Errors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADriveRelativePath_Should_BeReported_OnEverySystem(bool windowsHost)
    {
        var (path, error) = One("C:lib/File.dll", _script, windowsHost);

        Assert.Null(path);
        Assert.Contains("relative to the current folder of a Windows drive", error);
        Assert.Contains("'C:/Libraries/Library.dll'", error);
    }

    [Fact]
    public void AWindowsPath_OnWindows_Should_ResolveToItsOwnDestination()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(Path.GetFullPath("C:\\Lib\\A.dll"), One("C:/Lib/A.dll", _script, windowsHost: true).Path);
        Assert.Equal(Path.GetFullPath("\\\\server\\share\\A.dll"), One("//server/share/A.dll", _script, windowsHost: true).Path);
    }

    // Untitled: no folder to be relative to — reported instead of picking some other directory.
    [Theory]
    [InlineData("lib/Greeter.dll", "is relative to the script's folder: save the script first")]
    [InlineData("$(CurrentQueryPath)/lib/Greeter.dll", "uses $(CurrentQueryPath), the script's folder: save the script first")]
    public void AnUntitledScript_Should_ReportReferencesNeedingItsFolder(string reference, string message)
    {
        var resolved = ReferencePathResolver.Resolve([reference], scriptPath: null);

        Assert.Empty(resolved.Assemblies);
        Assert.Contains(message, Assert.Single(resolved.Errors));
    }

    [Fact]
    public void AnUntitledScript_Should_StillUseAbsoluteReferences()
    {
        var library = Library("lib", "Greeter", "public static class Greeter { }");

        Assert.Equal([Path.GetFullPath(library)], ReferencePathResolver.Resolve([library], scriptPath: null).Assemblies);
    }

    [Fact]
    public void Macros_Should_ExpandAtResolution_AndCurrentQueryPathMeanTheScriptsFolder()
    {
        var library = Library("lib", "Greeter", "public static class Greeter { }");

        var resolved = ReferencePathResolver.Resolve(["$(CurrentQueryPath)/lib/Greeter.dll", "$(RuntimeDirectory)/System.Runtime.dll"], _script);

        Assert.Equal(Path.GetFullPath(library), resolved.Assemblies[0]);
        Assert.EndsWith("System.Runtime.dll", resolved.Assemblies[1]);
        Assert.Empty(resolved.Errors);
    }

    [Fact]
    public void AMissingFile_Should_BeAWarning_WithTheResolvedPath()
    {
        var resolved = ReferencePathResolver.Resolve(["lib/Missing.dll"], _script);

        Assert.Empty(resolved.Assemblies);
        Assert.Contains($"was not found at '{Path.Combine(_scriptDir, "lib", "Missing.dll")}'", Assert.Single(resolved.Warnings));
    }

    [Fact]
    public void AMissingFileWithABackslash_OnUnix_Should_SayWhatTheBackslashMeansHere()
    {
        if (OperatingSystem.IsWindows()) return;
        var resolved = ReferencePathResolver.Resolve(["lib\\Missing.dll"], _script);

        Assert.Contains("'\\' is part of a file name", Assert.Single(resolved.Warnings));
    }

    // ── The same file for the build, the CLI and IntelliSense ────────────────

    [Fact]
    public async Task TheBuild_TheCli_AndIntelliSense_Should_UseTheScriptRelativeReference()
    {
        var library = Library("lib", "Greeter", "namespace Shared { public static class Greeter { public static string Hello() => \"hi\"; } }");
        var frontMatter = new MdRunFrontMatter { References = ["lib/Greeter.dll"] };
        const string code = "System.Console.WriteLine(Shared.Greeter.Hello());\n";
        var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";

        // The CLI's options (EditorTabItemControl builds the GUI's from the same resolver).
        var resolved = ReferencePathResolver.Resolve(frontMatter.References, _script);
        Assert.Equal([Path.GetFullPath(library)], resolved.Assemblies);

        // A real build with them.
        var output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk(tfm, tfm, tfm[3..] + ".0", tfm),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = output,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = output,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = MdRunKind.Statements,
            AssemblyReferences = resolved.Assemblies,
            ReferenceErrors = resolved.Errors,
            ReferenceWarnings = resolved.Warnings,
            CurrentQueryPath = _script,
        };
        using (var compiler = new CSharpScriptCompiler(options, NullLogger.Instance))
        {
            var build = await compiler.Compilation(code, TestContext.Current.CancellationToken);
            Assert.True(build.Success, string.Join("\n", build.Errors.Select(e => e.Message)));
        }

        // IntelliSense with the same resolution: no errors; without it, the type is unknown.
        var referenceService = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
        Assert.NotEmpty(await ErrorsAsync(service, code));
        service.UpdatePackageReferences(new Dictionary<string, Package>(),
            new PackageReferenceContext(tfm, null, null, "dotnet", resolved.Assemblies));
        Assert.Empty(await ErrorsAsync(service, code));
    }

    // A reference that can't be used fails the build with its message instead of being ignored.
    [Fact]
    public async Task TheBuild_Should_FailWithTheReferenceError()
    {
        var tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var output = Directory.CreateDirectory(Path.Combine(_root, "out2")).FullName;
        var resolved = ReferencePathResolver.Resolve(["C:lib/File.dll", "lib/Missing.dll"], _script);
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk(tfm, tfm, tfm[3..] + ".0", tfm),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = output,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = output,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = MdRunKind.Statements,
            ReferenceErrors = resolved.Errors,
            ReferenceWarnings = resolved.Warnings,
        };
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);

        var build = await compiler.Compilation("System.Console.WriteLine(1);\n", TestContext.Current.CancellationToken);

        Assert.False(build.Success);
        Assert.Contains("relative to the current folder of a Windows drive", Assert.Single(build.Errors).Message);
        Assert.Contains("was not found", Assert.Single(build.Warnings).Message);
    }

    private static async Task<List<string>> ErrorsAsync(CSharpIntelliSenseService service, string code)
    {
        var result = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDiagnostics(string analyzed, IReadOnlyList<IDiagnosticInfo> diagnostics)
        {
            if (analyzed == code) result.TrySetResult(diagnostics);
        }
        service.DiagnosticsChanged += OnDiagnostics;
        try
        {
            service.NotifyTextChanged(code);
            var diagnostics = await result.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList();
        }
        finally
        {
            service.DiagnosticsChanged -= OnDiagnostics;
        }
    }
}
