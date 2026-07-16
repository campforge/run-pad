using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #88: CompileInternalAsync used to always `new MsBuildProcessCompiler(...)` directly, so
// exercising CompileAsync's own logic (kind-transform wiring, ProcessCompilerResult → ICompilationResult
// mapping) meant either shelling out to a real `dotnet build` or not testing it at all. The injected
// DotnetProcessCompilerFactory lets a fake compiler stand in instead — no disk/process involved.
public class CompiledDotnetLanguageHostCompilerFactoryTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public string? ReceivedCode { get; private set; }

        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx)
        {
            ReceivedCode = code;
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }

    private static (CSharpLanguageHost Host, FakeCompiler Compiler) CreateHost(
        RpadKind kind, ProcessCompilerResult result, out string outputDir)
    {
        outputDir = Path.Combine(Path.GetTempPath(), "dotpad-tests-" + Guid.NewGuid());
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = "DotPad.Runtime.dll",
            Kind = kind,
        };

        var compiler = new FakeCompiler(result);
        var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance, (_, _, _) => compiler);
        return (host, compiler);
    }

    [Fact]
    public async Task CompileAsync_Should_UseTheInjectedCompiler_InsteadOfShellingOutToDotnetBuild()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(RpadKind.Statements, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync("int x = 1;", TestContext.Current.CancellationToken);

            Assert.True(compilation.Success);
            Assert.Equal("int x = 1;", compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_PassTheKindTransformedCode_ToTheInjectedCompiler()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(RpadKind.Expression, result, out var outputDir);
        try
        {
            await host.CompileAsync("1 + 1", TestContext.Current.CancellationToken);

            Assert.Equal("(\n1 + 1\n).Dump();\n", compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_SurfaceCompilerFailure_WithoutInvokingARealBuild()
    {
        var result = new ProcessCompilerResult(
            OutputBinaryFile: "",
            Success: false,
            Errors: [new ResultItem("CS0000", "Program.cs", new LineSymbol(1, 1), "boom")],
            Warnings: []);
        var (host, _) = CreateHost(RpadKind.Statements, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync("this is not valid C#", TestContext.Current.CancellationToken);

            Assert.False(compilation.Success);
            Assert.Single(compilation.Diagnostics);
            Assert.Equal("boom", compilation.Diagnostics[0].Message);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
