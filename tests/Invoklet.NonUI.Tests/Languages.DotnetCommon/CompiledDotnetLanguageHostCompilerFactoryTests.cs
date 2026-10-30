using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.DotnetCommon;

// CompileInternalAsync used to always `new MsBuildProcessCompiler(...)` directly, so
// exercising CompileAsync's own logic (kind-transform wiring, ProcessCompilerResult → ICompilationResult
// mapping) meant either a real build or not testing it at all. The injected
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
        MdRunKind kind, ProcessCompilerResult result, out string outputDir)
    {
        outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());
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
            RuntimeLibPath = "Invoklet.Runtime.dll",
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
        var (host, compiler) = CreateHost(MdRunKind.Statements, result, out var outputDir);
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
        var (host, compiler) = CreateHost(MdRunKind.Expression, result, out var outputDir);
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
        var (host, _) = CreateHost(MdRunKind.Statements, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync("this is not valid C#", TestContext.Current.CancellationToken);

            Assert.False(compilation.Success);
            Assert.Single(compilation.Diagnostics);
            Assert.Equal("boom", compilation.Diagnostics[0].Message);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Theory]
    [InlineData(MdRunKind.Statements, "class __InvokletProgram { }")]
    [InlineData(MdRunKind.Program, "class __InvokletProgram { static void Main() { } }")]
    [InlineData(MdRunKind.Expression, "class __InvokletProgram { }")]
    public async Task CompileAsync_Should_RejectTheReservedProgramWrapperType_BeforeTheKindTransform(
        MdRunKind kind, string code)
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(kind, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync(code, TestContext.Current.CancellationToken);

            Assert.False(compilation.Success);
            var diagnostic = Assert.Single(compilation.Diagnostics);
            Assert.Equal("RPAD1001", diagnostic.Code);
            Assert.Contains("'__InvokletProgram' is reserved", diagnostic.Message, StringComparison.Ordinal);
            Assert.Null(compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_RejectTheReservedProgramTrampolineMethod()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(MdRunKind.Program, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync(
                "void __InvokletRun() { }\nvoid Main() { }", TestContext.Current.CancellationToken);

            Assert.False(compilation.Success);
            var diagnostic = Assert.Single(compilation.Diagnostics);
            Assert.Equal("RPAD1001", diagnostic.Code);
            Assert.Contains("'__InvokletRun' is reserved", diagnostic.Message, StringComparison.Ordinal);
            Assert.Null(compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_RejectTheReservedProgramTrampolineField()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(MdRunKind.Program, result, out var outputDir);
        try
        {
            var compilation = await host.CompileAsync(
                "int __InvokletRun;\nvoid Main() { }", TestContext.Current.CancellationToken);

            Assert.False(compilation.Success);
            var diagnostic = Assert.Single(compilation.Diagnostics);
            Assert.Equal("RPAD1001", diagnostic.Code);
            Assert.Contains("'__InvokletRun' is reserved", diagnostic.Message, StringComparison.Ordinal);
            Assert.Null(compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_AllowTheTrampolineName_ForAProgramLocalFunction()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(MdRunKind.Program, result, out var outputDir);
        try
        {
            const string code = "void Main() { void __InvokletRun() { } __InvokletRun(); }";
            var compilation = await host.CompileAsync(code, TestContext.Current.CancellationToken);

            Assert.True(compilation.Success);
            Assert.NotNull(compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_NotRejectReservedNamesInsideStringsOrComments()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, compiler) = CreateHost(MdRunKind.Statements, result, out var outputDir);
        try
        {
            const string code = "// class __InvokletProgram { }\nConsole.WriteLine(\"__InvokletProgram\");";
            var compilation = await host.CompileAsync(code, TestContext.Current.CancellationToken);

            Assert.True(compilation.Success);
            Assert.Equal(code, compiler.ReceivedCode);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The "IL" output tab reads the compiled binary path off ILanguageHost.Compiled.
    [Fact]
    public async Task CompileAsync_Should_RaiseCompiledEvent_WithTheOutputBinaryPath_OnSuccess()
    {
        var result = new ProcessCompilerResult("out.dll", true, [], []);
        var (host, _) = CreateHost(MdRunKind.Statements, result, out var outputDir);
        try
        {
            string? received = null;
            host.Compiled += path => received = path;

            await host.CompileAsync("int x = 1;", TestContext.Current.CancellationToken);

            Assert.Equal("out.dll", received);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task CompileAsync_Should_NotRaiseCompiledEvent_OnFailure()
    {
        var result = new ProcessCompilerResult(
            OutputBinaryFile: "",
            Success: false,
            Errors: [new ResultItem("CS0000", "Program.cs", new LineSymbol(1, 1), "boom")],
            Warnings: []);
        var (host, _) = CreateHost(MdRunKind.Statements, result, out var outputDir);
        try
        {
            var raised = false;
            host.Compiled += _ => raised = true;

            await host.CompileAsync("this is not valid C#", TestContext.Current.CancellationToken);

            Assert.False(raised);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A diagnostic with no source position (line 0 — "CSC : error CS5001", MSBuild-level errors) must
    // not be printed with a fake "(0,0):" location.
    [Theory]
    [InlineData(1, 5, "CS0103", "(1,5): error CS0103: boom")]
    [InlineData(0, 0, "CS5001", "error CS5001: boom")]
    [InlineData(0, 0, "", "error: boom")]
    public async Task RunAsync_Should_FormatCompileErrors_OmittingAMissingPosition(
        int line, int column, string code, string expected)
    {
        var result = new ProcessCompilerResult(
            OutputBinaryFile: "",
            Success: false,
            Errors: [new ResultItem(code, "", new LineSymbol((uint)line, (uint)column), "boom")],
            Warnings: []);
        var (host, _) = CreateHost(MdRunKind.Statements, result, out var outputDir);
        try
        {
            var output = new List<(string Text, OutputKind Kind)>();

            await host.RunAsync("x", (text, kind) => output.Add((text, kind)), TestContext.Current.CancellationToken);

            var line0 = Assert.Single(output);
            Assert.Equal(expected, line0.Text);
            Assert.Equal(OutputKind.StandardError, line0.Kind);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
