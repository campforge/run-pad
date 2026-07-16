using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.DotnetCommon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #66: CalcKindOffset() returned 2 for RpadKind.Program, but ApplyKindTransform's
// Program branch ("Main();{code}", no newline) glues onto the same physical line as the user's first
// line — structurally zero new lines, same as Statements' passthrough — which misaligned every
// breakpoint/stack frame in Program-kind scripts by exactly the erroneous offset. These tests pin the
// exact transformed text and offset for all three kinds directly against the real (internal)
// CSharpLanguageHost members, rather than only inferring correctness from DapSession's line math.
public class CSharpLanguageHostKindTransformTests
{
    // CSharpLanguageHost only implements IAsyncDisposable (see LanguageHostBase) — nothing here starts
    // a process or opens a handle (only Directory.CreateDirectory in the ctor for OutputDirectory), so
    // these tests don't bother disposing; the temp directory is cleaned up explicitly instead.
    private static CSharpLanguageHost CreateHost(RpadKind kind, out string outputDir, IReadOnlyList<string>? usings = null)
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
            Usings = usings ?? [],
        };
        return new CSharpLanguageHost(options, NullLoggerFactory.Instance);
    }

    [Theory]
    [InlineData(RpadKind.Statements, "int x = 1;", "int x = 1;")]
    [InlineData(RpadKind.Program, "void Main() { }", "Main();void Main() { }")]
    [InlineData(RpadKind.Expression, "1 + 1", "(\n1 + 1\n).Dump();\n")]
    public void ApplyKindTransform_Should_ProduceExpectedText(RpadKind kind, string code, string expected)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expected, host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The regression itself: Program used to report offset 2 while its transform inserts zero actual
    // lines (the "Main();" prefix has no trailing newline) — same structural shape as Statements'
    // untouched passthrough, hence the same offset.
    [Theory]
    [InlineData(RpadKind.Statements, 0)]
    [InlineData(RpadKind.Program, 0)]
    [InlineData(RpadKind.Expression, 1)]
    public void CalcKindOffset_Should_MatchLinesActuallyInsertedByApplyKindTransform(RpadKind kind, int expectedOffset)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expectedOffset, host.CalcKindOffset());
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Cross-check tying the two together: for every kind, the number of '\n' characters that appear
    // BEFORE the user's own code in the transformed text must equal CalcKindOffset() — this is the
    // actual invariant DapSession relies on (see its _lineOffset doc comment), not just two isolated
    // hardcoded expectations that could coincidentally both be updated wrong in the same way.
    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void CalcKindOffset_Should_EqualNewlinesInsertedBeforeUserCodeByApplyKindTransform(RpadKind kind)
    {
        const string code = "FIRST_USER_LINE";
        var host = CreateHost(kind, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            var indexOfUserCode = transformed.IndexOf(code, StringComparison.Ordinal);
            Assert.True(indexOfUserCode >= 0);

            var newlinesBeforeUserCode = transformed[..indexOfUserCode].Count(c => c == '\n');
            Assert.Equal(newlinesBeforeUserCode, host.CalcKindOffset());
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Follow-up regression: DotnetProcessCompiler.Compilation separately prepends one "using X;" line
    // per front-matter namespace BEFORE writing the .cs file that's actually compiled/debugged (its
    // own "Prepend global usings" step) — a real, kind-independent source of line drift that
    // CalcKindOffset previously ignored entirely, for every kind, not just Program. Reported live by
    // the user immediately after the kind-transform-only fix above ("now the debugger misses by
    // exactly one line") on a script whose front matter has one namespace configured.
    [Theory]
    [InlineData(RpadKind.Statements, 0)]
    [InlineData(RpadKind.Program, 0)]
    [InlineData(RpadKind.Expression, 1)]
    public void CalcKindOffset_Should_IncludeFrontMatterUsingsCount(RpadKind kind, int kindOnlyOffset)
    {
        IReadOnlyList<string> usings = ["System.Text", "System.Linq"];
        var host = CreateHost(kind, out var outputDir, usings);
        try
        {
            Assert.Equal(usings.Count + kindOnlyOffset, host.CalcKindOffset());
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
