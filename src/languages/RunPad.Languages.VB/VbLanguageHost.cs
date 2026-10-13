using Microsoft.Extensions.Logging;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;

namespace RunPad.Languages.VB;

public sealed class VbLanguageHost : CompiledDotnetLanguageHostBase
{
    // Single source of truth — see CSharpLanguageHost.SupportedCapabilities.
    // No IntelliSense flag yet — VB tabs compile/run/debug but have no autocomplete/diagnostics/
    // quick-fixes (would need a VisualBasicIntelliSenseService against Microsoft.CodeAnalysis.VisualBasic).
    public const LanguageHostCapabilities SupportedCapabilities =
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump;

    public override string Language => "vb";

    public override LanguageHostCapabilities Capabilities => SupportedCapabilities;

    protected override DotnetLanguageProjectProfile ProjectProfile => VbProjectProfile.Instance;

    internal VbLanguageHost(
        CompiledDotnetLanguageHostOptions options,
        ILoggerFactory loggerFactory,
        DotnetProcessCompilerFactory? compilerFactory = null,
        ExecutionChildProcessFactory? executionProcessFactory = null,
        DebuggerChildProcessFactory? debuggerProcessFactory = null)
        : base(options, loggerFactory, compilerFactory, executionProcessFactory, debuggerProcessFactory)
    {
    }

    protected override IDotnetProcessCompiler CreateCompiler(CompiledDotnetLanguageHostOptions options, ILoggerFactory loggerFactory) =>
        new VbScriptCompiler(options, loggerFactory.CreateLogger<VbScriptCompiler>());

    // ── Kind transform ───────────────────────────────────────────────────────

    // VB has no top-level-statements feature (unlike C# 9+), so every runnable entry point needs an
    // explicit Module/Sub Main — the two kinds effectively swap roles versus CSharpLanguageHost:
    //   Statements → wrap in a generated Sub Main (user writes bare statements, no boilerplate).
    //   Program    → passthrough (user supplies their own full Module ... Sub Main ... End Module).
    //   Expression → wrap as (code).Dump() — no trailing semicolon, VB's statement separator is a
    //                newline, not ';'.
    public override string ApplyKindTransform(string code) => Options.Kind switch
    {
        RpadKind.Statements => $"Module Program\nSub Main()\n{code}\nEnd Sub\nEnd Module\n",
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump()\n",
        _ => code,
    };

    // Lines prepended before the user's first line, both by ApplyKindTransform (above) and by
    // the compiler (RoslynScriptCompiler), which separately prepends one "Imports X" line per
    // front-matter namespace before writing the .vb file that's actually compiled/debugged — a
    // real, kind-independent source of line drift, not accounted for by the kind-transform
    // component alone (see CSharpLanguageHost.CalcKindOffset's identical fix).
    public override int CalcKindOffset() => Options.Usings.Count + (Options.Kind switch
    {
        RpadKind.Statements => 2, // "Module Program" + "Sub Main()"
        RpadKind.Expression => 1, // "("
        _ => 0,
    });
}
