using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Languages.VB;
using Invoklet.NonUI.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.DotnetCommon.Adapters;

// Roslyn reports positions in the generated script file (front-matter imports plus the kind
// transform's wrapper come first). The host translates the ones in that file back to the user's
// code; these tests feed it positions as the compiler reports them, with no real build.
public class CompiledDotnetDiagnosticLineMappingTests
{
    private const string ScriptFile = "/InvokletQuery";

    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    private static CompiledDotnetLanguageHostOptions Options(string outputDir, MdRunKind kind, int usings,
        DotnetSdk? platform = null, string runtimeLibPath = "Invoklet.Runtime.dll") => new()
        {
            Platform = platform ?? new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = runtimeLibPath,
            Kind = kind,
            Usings = [.. Enumerable.Range(0, usings).Select(i => "System.Ns" + i)],
        };

    private static async Task<IReadOnlyList<(string Code, string File, int Line, int Column)>> Compile(
        bool vb, MdRunKind kind, int usings, string code, params ResultItem[] errors)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());
        try
        {
            var compiler = new FakeCompiler(new ProcessCompilerResult("", false, errors, []));
            var options = Options(outputDir, kind, usings);
            CompiledDotnetLanguageHostBase host = vb
                ? new VbLanguageHost(options, NullLoggerFactory.Instance, (_, _, _) => compiler)
                : new CSharpLanguageHost(options, NullLoggerFactory.Instance, (_, _, _) => compiler);
            await using (host)
            {
                var result = await host.CompileAsync(code, TestContext.Current.CancellationToken);
                return [.. result.Diagnostics.Select(d => (d.Code, d.File, d.Line, d.Column))];
            }
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    private static ResultItem Item(string extension, int line, int column, string file = "") =>
        new("CS0103", file.Length == 0 ? ScriptFile + extension : file, new LineSymbol((uint)line, (uint)column), "boom");

    [Fact]
    public async Task CSharpProgram_Should_MapTheWrapperAndFrontMatterOffsetBack()
    {
        // 1 import + the call statement/class header line + the brace line: user line 3 is generated line 6.
        var diagnostics = await Compile(false, MdRunKind.Program, 1, "void Main()\n{\n    MissingName();\n}\n", Item(".cs", 6, 5));

        Assert.Equal((3, 5), (diagnostics[0].Line, diagnostics[0].Column));
    }

    [Theory]
    [InlineData(MdRunKind.Statements, 2, 3, 1)]
    [InlineData(MdRunKind.Statements, 0, 2, 2)]
    [InlineData(MdRunKind.Expression, 1, 3, 1)]
    public async Task CSharp_Should_MapKindAndImportLinesBack(MdRunKind kind, int usings, int generatedLine, int userLine)
    {
        var diagnostics = await Compile(false, kind, usings, "a\nb\nc\n", Item(".cs", generatedLine, 7));

        Assert.Equal((userLine, 7), (diagnostics[0].Line, diagnostics[0].Column));
    }

    [Theory]
    [InlineData(MdRunKind.Statements, 1, 4, 1)]
    [InlineData(MdRunKind.Statements, 0, 4, 2)]
    [InlineData(MdRunKind.Expression, 1, 3, 1)]
    [InlineData(MdRunKind.Program, 1, 3, 2)]
    public async Task Vb_Should_MapKindAndImportLinesBack(MdRunKind kind, int usings, int generatedLine, int userLine)
    {
        var diagnostics = await Compile(true, kind, usings, "a\nb\nc\n", Item(".vb", generatedLine, 3));

        Assert.Equal((userLine, 3), (diagnostics[0].Line, diagnostics[0].Column));
    }

    [Fact]
    public async Task CSharpProgram_Should_KeepHoistedLeadingDirectivesOnTheirOriginalLines()
    {
        // Generated: import / directives + call statement + class header / brace / user lines.
        const string code = "// header\n  using System.Text;\nusing System.Linq;\nvoid Main()\n{\n    Missing();\n}\n";
        var diagnostics = await Compile(false, MdRunKind.Program, 1, code,
            Item(".cs", 2, 7),   // `System` of the first hoisted using: written on line 2, column 9
            Item(".cs", 3, 7),   // second hoisted using
            Item(".cs", 3, 40),  // the call statement glued after it
            Item(".cs", 4, 1),   // the wrapper's opening brace
            Item(".cs", 1, 1),   // the front-matter import
            Item(".cs", 10, 5)); // `Missing();`, user line 6

        Assert.Equal([(2, 9), (3, 7), (0, 0), (0, 0), (0, 0), (6, 5)],
            diagnostics.Select(d => (d.Line, d.Column)).ToList());
    }

    [Fact]
    public async Task Diagnostics_Should_NotBeShifted_WhenOutsideTheUserScript()
    {
        var diagnostics = await Compile(false, MdRunKind.Program, 1, "void Main()\n{\n}\n",
            Item(".cs", 6, 5, "/obj/InvokletQuery.Generated.cs"),
            Item(".cs", 4, 2, "/obj/Connections/Db.cs"),
            Item(".cs", 3, 1, "/Other/InvokletQuery.cs"),
            new ResultItem("CS5001", "", new LineSymbol(0, 0), "no position"));

        Assert.Equal([(6, 5), (4, 2), (3, 1), (0, 0)], diagnostics.Select(d => (d.Line, d.Column)).ToList());
    }

    [Fact]
    public async Task Diagnostics_Should_LoseTheLine_WhenTheyPointAtGeneratedText()
    {
        // Imports before the user's code and the Expression/Statements suffix after it.
        var expression = await Compile(false, MdRunKind.Expression, 1, "1 +\n2\n\n", Item(".cs", 1, 1), Item(".cs", 5, 2));
        var vb = await Compile(true, MdRunKind.Statements, 0, "x = 1", Item(".vb", 2, 1), Item(".vb", 4, 1));

        Assert.All(expression.Concat(vb), d => Assert.Equal((0, 0), (d.Line, d.Column)));
    }

    // The confirmed report, end to end through the real compiler.
    [Trait("Category", "RealBuild")]
    [Collection(RealBuildTestCollection.Name)]
    public class RealBuild
    {
        [Fact]
        public async Task CSharpProgram_Should_ReportErrorsOnTheUsersLine()
        {
            var outputDir = Directory.CreateTempSubdirectory("Invoklet-line-mapping-test").FullName;
            try
            {
                var options = Options(outputDir, MdRunKind.Program, 1, new DotnetSdkProvider().Get().First(),
                    new CSharpReferenceService().GetExtensionReference());
                await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);

                var result = await host.CompileAsync("void Main()\n{\n    MissingName();\n}\n", CancellationToken.None);

                var error = Assert.Single(result.Diagnostics, d => d.Code == "CS0103");
                Assert.Equal((3, 5), (error.Line, error.Column));
            }
            finally { Directory.Delete(outputDir, recursive: true); }
        }
    }
}
