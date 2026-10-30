using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.NonUI.Tests.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

public sealed class DuplicateFrameworkReferencesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-duplicate-framework").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void IdenticalFrameworkReferences_Should_BeKeptOnlyOnce()
    {
        var path = typeof(Console).Assembly.Location;
        var result = RoslynScriptCompiler.ResolveConflicts([path, path], []);
        Assert.Equal([path], result.Framework);
        Assert.Empty(result.Packages);
    }

    [Fact]
    public void EqualVersionFrameworkCopies_Should_PreferTheFirstReference()
    {
        var original = typeof(Console).Assembly.Location;
        var copy = Path.Combine(_root, "System.Console.dll");
        File.Copy(original, copy);
        var result = RoslynScriptCompiler.ResolveConflicts([original, copy], []);
        Assert.Equal([original], result.Framework);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameworkCopies_Should_PreserveTheFirstTargetReference_EvenWhenAnotherCopyIsNewer(bool reverse)
    {
        var oldDir = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        var newDir = Directory.CreateDirectory(Path.Combine(_root, "new")).FullName;
        var older = ScriptCompilerTests.CompileLibrary(oldDir, "Overlap", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class Old {}", []);
        var newer = ScriptCompilerTests.CompileLibrary(newDir, "Overlap", "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class New {}", []);
        var result = RoslynScriptCompiler.ResolveConflicts(reverse ? [newer, older] : [older, newer], []);
        Assert.Equal([reverse ? newer : older], result.Framework);
    }

    [Fact]
    public async Task EditorTargetRefresh_WithAnAppLocalFrameworkCopy_Should_NotCrashOrDuplicateTypes()
    {
        var copy = Path.Combine(_root, "System.Console.dll");
        File.Copy(typeof(Console).Assembly.Location, copy);
        var references = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(references.GetDefault()
            .Append(references.GetExtensionReference()).Append(copy), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
        service.UpdatePackageReferences(new Dictionary<string, Package>(),
            new PackageReferenceContext($"net{Environment.Version.Major}.{Environment.Version.Minor}", null, null, "dotnet"));
        var done = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string code = "Console.WriteLine(42);";
        service.DiagnosticsChanged += (text, diagnostics) => { if (text == code) done.TrySetResult(diagnostics); };
        service.NotifyTextChanged(code);
        var diagnostics = await done.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
