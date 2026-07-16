using DotPad.Contracts;
using DotPad.Format.RPad;
using DotPad.Languages.DotnetCommon;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHost : CompiledDotnetLanguageHostBase
{
    // Single source of truth for what this host actually supports — CSharpLanguageHostFactory.
    // Capabilities reads this instead of declaring its own copy, so the registry's
    // GetCapabilities("csharp") (checkable before a host is even created) can't silently drift from
    // what the created host itself reports, the way it used to (missing IntelliSense — ANALYSIS.md
    // #96).
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

    internal CSharpLanguageHost(CompiledDotnetLanguageHostOptions options, ILoggerFactory loggerFactory)
        : base(options, loggerFactory)
    {
    }

    // ── Kind transform ───────────────────────────────────────────────────────

    public override string ApplyKindTransform(string code) => Options.Kind switch
    {
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump();\n",
        RpadKind.Program => $"{RpadKindTransform.ProgramMainCallPrefix}{code}",
        _ => code,
    };

    // Lines prepended before the user's first line, both by ApplyKindTransform (above) and by
    // MsBuildProcessCompiler.Compilation, which separately prepends one "using X;" line per
    // front-matter namespace (see its own "Prepend global usings/imports" step) BEFORE writing the
    // .cs file that's actually compiled/debugged — a real, per-script, kind-independent source of
    // line drift, on top of the kind-transform offset itself.
    //
    // The kind-transform component must match ApplyKindTransform exactly so DapSession can map a
    // breakpoint set on the user-visible line to the right line in the actually-compiled
    // (transformed) source. Program's prefix has no trailing newline (it's glued onto the same
    // physical line as the user's own first line), so it inserts zero new lines — same as
    // Statements' passthrough.
    public override int CalcKindOffset() => Options.Usings.Count + (Options.Kind switch
    {
        RpadKind.Expression => 1,
        _ => 0,
    });
}
