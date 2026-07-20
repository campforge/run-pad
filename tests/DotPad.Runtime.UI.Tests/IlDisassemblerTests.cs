using DotPad.Build.Core.Models;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 — the "IL" output tab (mirrors LINQPad's IL view). Real dotnet-build integration test
// (no mocking of the compiler): the thing under test is whether ICSharpCode.Decompiler can actually
// read a binary MsBuildProcessCompiler just produced, which pure-logic unit testing can't stand in for.
public class IlDisassemblerTests
{
    private static MsBuildProcessCompiler CreateCompiler(string outputDir)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new MsBuildProcessCompiler(
            CSharpProjectProfile.Instance,
            sdk: sdk,
            executionOptions: new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            outputDir: outputDir,
            dotPadRuntimeLib: runtimeLib,
            outputName: "Query",
            nugetPackages: new Dictionary<string, string>(),
            usings: [],
            assemblyReferences: [],
            projectReferences: [],
            logger: NullLogger<MsBuildProcessCompiler>.Instance);
    }

    [Fact]
    public async Task Disassemble_Should_ProduceIlText_ContainingACompiledMethodByName()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-il-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            const string code = "IlDisassemblerProbe.ThisNameMustAppearInTheIl();\n"
                                 + "class IlDisassemblerProbe { public static void ThisNameMustAppearInTheIl() { System.Console.WriteLine(1); } }\n";
            var result = await compiler.Compilation(code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var il = IlDisassembler.Disassemble(result.OutputBinaryFile);

            Assert.Contains("ThisNameMustAppearInTheIl", il);
            Assert.Contains("IlDisassemblerProbe", il);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
