using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;

namespace RunPad.Languages.CSharp;

public sealed class CSharpLanguageHost : CompiledDotnetLanguageHostBase
{
    // Single source of truth for what this host actually supports — CSharpLanguageHostFactory.
    // Capabilities reads this instead of declaring its own copy, so the registry's
    // GetCapabilities("csharp") (checkable before a host is even created) can't silently drift from
    // what the created host itself reports, the way it used to (missing IntelliSense).
    public const LanguageHostCapabilities SupportedCapabilities =
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump |
        LanguageHostCapabilities.IntelliSense;

    public override string Language => "csharp";

    public override LanguageHostCapabilities Capabilities => SupportedCapabilities;

    protected override DotnetLanguageProjectProfile ProjectProfile => CSharpProjectProfile.Instance;

    internal CSharpLanguageHost(
        CompiledDotnetLanguageHostOptions options,
        ILoggerFactory loggerFactory,
        DotnetProcessCompilerFactory? compilerFactory = null,
        ExecutionChildProcessFactory? executionProcessFactory = null,
        DebuggerChildProcessFactory? debuggerProcessFactory = null)
        : base(options, loggerFactory, compilerFactory, executionProcessFactory, debuggerProcessFactory)
    {
    }

    protected override IDotnetProcessCompiler CreateCompiler(CompiledDotnetLanguageHostOptions options, ILoggerFactory loggerFactory) =>
        new CSharpScriptCompiler(options, loggerFactory.CreateLogger<CSharpScriptCompiler>());

    // ── Kind transform ───────────────────────────────────────────────────────

    public override string ApplyKindTransform(string code) => Options.Kind switch
    {
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump();\n",
        RpadKind.Program => $"{RpadKindTransform.ProgramPrefixFor(code, ParseOptions())}{code}{RpadKindTransform.ProgramSuffixFor(code, ParseOptions())}",
        _ => RpadKindTransform.StatementsFor(code, ParseOptions()),
    };

    // The options the build parses with, for the target framework the script runs on.
    private CSharpParseOptions ParseOptions() =>
        TargetFramework.TryParse(Options.Platform.TargetFrameworkMoniker, out var major, out var minor)
            ? CSharpScriptCompiler.ParseOptions(major, minor)
            : CSharpScriptCompiler.ParseOptions(Environment.Version.Major, Environment.Version.Minor);

    // Lines prepended before the user's first line, both by ApplyKindTransform (above) and by the
    // compiler (RoslynScriptCompiler), which separately prepends one "using X;" line per
    // front-matter namespace BEFORE writing the
    // .cs file that's actually compiled/debugged — a real, per-script, kind-independent source of
    // line drift, on top of the kind-transform offset itself.
    //
    // The kind-transform component must match ApplyKindTransform exactly so DapSession can map a
    // breakpoint set on the user-visible line to the right line in the actually-compiled
    // (transformed) source. Program's prefix (see RpadKindTransform.ProgramPrefixFor), when there is
    // one (a program compiled as written has none), inserts exactly 2 new lines before the user's own first line — the call statement itself
    // has no trailing newline (glued onto the same physical line as the wrapper class's own header),
    // but the class header and its opening brace each occupy their own line, regardless of which call
    // variant was actually picked. ApplyKindTransform's own suffix (the class's closing brace) comes
    // AFTER the user's code and so never affects this offset.
    // A Program compiled as written (no top-level statements) gets no prefix at all.
    public override int CalcKindOffset(string code) => Options.Usings.Count + (Options.Kind switch
    {
        RpadKind.Expression => 1,
        RpadKind.Program => RpadKindTransform.ProgramLineOffset(code, ParseOptions()),
        _ => 0,
    });
}
