using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.VB;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// VbLanguageHost.ApplyKindTransform/
// CalcKindOffset had zero test coverage — unlike CSharpLanguageHost (see
// CSharpLanguageHostKindTransformTests), nothing pinned VB's transformed text/offset, including the
// claim (made when the async-Main fix landed) that Program's passthrough already handles
// `Async Function Main() As Task` natively without any host changes.
public class VbLanguageHostKindTransformTests
{
    private static VbLanguageHost CreateHost(MdRunKind kind, out string outputDir, IReadOnlyList<string>? usings = null)
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
            Usings = usings ?? [],
        };
        return new VbLanguageHost(options, NullLoggerFactory.Instance);
    }

    [Theory]
    [InlineData(MdRunKind.Statements, "Dim x = 1", "Module Program\nSub Main(args As String())\nDim x = 1\nEnd Sub\nEnd Module\n")]
    [InlineData(MdRunKind.Program, "Module Program\nSub Main()\nEnd Sub\nEnd Module", "Module Program\nSub Main()\nEnd Sub\nEnd Module")]
    [InlineData(MdRunKind.Expression, "1 + 1", "(\n1 + 1\n).Dump()\n")]
    public void ApplyKindTransform_Should_ProduceExpectedText(MdRunKind kind, string code, string expected)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expected, host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Program is a pure passthrough for VB (unlike C#, which prepends a "Main();"/"await Main();"
    // call) — the user supplies their own full Module/Sub Main, so an async entry point needs no
    // host-side transform at all. This is the regression test the async-Task-Main fix left as
    // an explicit gap: a claim from reading the code, not from a test.
    [Fact]
    public void ApplyKindTransform_Should_PassThroughAsyncFunctionMain_Unchanged_InProgramKind()
    {
        const string code = "Module Program\nAsync Function Main() As Task\nAwait Task.Delay(1)\nEnd Function\nEnd Module";
        var host = CreateHost(MdRunKind.Program, out var outputDir);
        try
        {
            Assert.Equal(code, host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Theory]
    [InlineData(MdRunKind.Statements, 2)]
    [InlineData(MdRunKind.Program, 0)]
    [InlineData(MdRunKind.Expression, 1)]
    public void CalcKindOffset_Should_MatchLinesActuallyInsertedByApplyKindTransform(MdRunKind kind, int expectedOffset)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expectedOffset, host.CalcKindOffset(""));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Same cross-check as CSharpLanguageHostKindTransformTests: the number of '\n' characters before
    // the user's own code in the transformed text must equal CalcKindOffset() — the actual invariant
    // DapSession relies on, not two independently-hardcoded numbers that could drift together.
    [Theory]
    [InlineData(MdRunKind.Statements)]
    [InlineData(MdRunKind.Program)]
    [InlineData(MdRunKind.Expression)]
    public void CalcKindOffset_Should_EqualNewlinesInsertedBeforeUserCodeByApplyKindTransform(MdRunKind kind)
    {
        const string code = "FIRST_USER_LINE";
        var host = CreateHost(kind, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            var indexOfUserCode = transformed.IndexOf(code, StringComparison.Ordinal);
            Assert.True(indexOfUserCode >= 0);

            var newlinesBeforeUserCode = transformed[..indexOfUserCode].Count(c => c == '\n');
            Assert.Equal(newlinesBeforeUserCode, host.CalcKindOffset(""));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Theory]
    [InlineData(MdRunKind.Statements, 2)]
    [InlineData(MdRunKind.Program, 0)]
    [InlineData(MdRunKind.Expression, 1)]
    public void CalcKindOffset_Should_IncludeFrontMatterUsingsCount(MdRunKind kind, int kindOnlyOffset)
    {
        IReadOnlyList<string> usings = ["System.Text", "System.Linq"];
        var host = CreateHost(kind, out var outputDir, usings);
        try
        {
            Assert.Equal(usings.Count + kindOnlyOffset, host.CalcKindOffset(""));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
