using DotPad.Build.Core.Models;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19) — external half. Real dotnet-build
// integration test (no mocking of the compiler): the thing under test is whether
// ICSharpCode.Decompiler can locate a SPECIFIC overload's declaration line inside a type it just
// decompiled from a binary MsBuildProcessCompiler produced, which pure-logic unit testing can't stand
// in for.
public class CSharpDecompilerServiceTests
{
    private sealed record FakeDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName) : IDefinitionResult;

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

    private const string Code = """
        IlDisassemblerProbe.Pick(1);
        IlDisassemblerProbe.Pick("x");
        class IlDisassemblerProbe
        {
            public static void Pick(int number) { System.Console.WriteLine(number); }
            public static void Pick(string text) { System.Console.WriteLine(text); }
        }
        """;

    [Fact]
    public async Task Decompile_Should_LocateTheExactOverload_DisambiguatedByParameterTypeNames()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(Code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var definition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "IlDisassemblerProbe", MemberName: "Pick",
                ParameterTypeNames: ["String"], DisplayName: "IlDisassemblerProbe.Pick(string)");

            var decompiled = CSharpDecompilerService.Decompile(definition);

            var lines = decompiled.Text.Split('\n');
            var line = lines[decompiled.Line - 1];
            Assert.Contains("Pick", line);
            Assert.Contains("string", line);
            Assert.DoesNotContain("int number", line);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task Decompile_Should_LocateTheType_WhenMemberNameIsNull()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(Code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var definition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "IlDisassemblerProbe", MemberName: null,
                ParameterTypeNames: [], DisplayName: "IlDisassemblerProbe");

            var decompiled = CSharpDecompilerService.Decompile(definition);

            var lines = decompiled.Text.Split('\n');
            Assert.Contains("class IlDisassemblerProbe", string.Join('\n', lines.Skip(decompiled.Line - 1).Take(3)));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
