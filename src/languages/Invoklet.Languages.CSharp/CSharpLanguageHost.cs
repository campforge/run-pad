using Invoklet.Languages.CSharp.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Invoklet.Contracts;
using Invoklet.Format.RPad;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;

namespace Invoklet.Languages.CSharp;

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

    protected override IReadOnlyList<ResultItem> ValidateUserCode(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, ParseOptions()).GetCompilationUnitRoot();
        var reserved = root.DescendantNodes()
            .Select(node => node switch
            {
                BaseTypeDeclarationSyntax type
                    when type.Identifier.ValueText == RpadKindTransform.ProgramWrapperClassName => type.Identifier,
                DelegateDeclarationSyntax @delegate
                    when @delegate.Identifier.ValueText == RpadKindTransform.ProgramWrapperClassName => @delegate.Identifier,
                _ => default,
            })
            .Where(identifier => identifier.RawKind != 0)
            .ToList();

        if (Options.Kind == RpadKind.Program)
        {
            foreach (var member in root.Members)
            {
                IEnumerable<SyntaxToken> identifiers = member switch
                {
                    GlobalStatementSyntax { Statement: LocalFunctionStatementSyntax function } => [function.Identifier],
                    GlobalStatementSyntax { Statement: LocalDeclarationStatementSyntax declaration } =>
                        declaration.Declaration.Variables.Select(variable => variable.Identifier),
                    BaseTypeDeclarationSyntax type => [type.Identifier],
                    DelegateDeclarationSyntax @delegate => [@delegate.Identifier],
                    MethodDeclarationSyntax method => [method.Identifier],
                    PropertyDeclarationSyntax property => [property.Identifier],
                    EventDeclarationSyntax @event => [@event.Identifier],
                    BaseFieldDeclarationSyntax field =>
                        field.Declaration.Variables.Select(variable => variable.Identifier),
                    _ => [],
                };
                reserved.AddRange(identifiers.Where(identifier =>
                    identifier.ValueText == RpadKindTransform.TrampolineMethodName));
            }
        }

        return [.. reserved.Select(ReservedIdentifierError)];
    }

    private ResultItem ReservedIdentifierError(SyntaxToken identifier)
    {
        var position = identifier.GetLocation().GetLineSpan().StartLinePosition;
        return new ResultItem(
            "RPAD1001",
            Path.GetFileName(SourceFilePath),
            new LineSymbol((uint)position.Line + 1, (uint)position.Character + 1),
            $"'{identifier.ValueText}' is reserved for Invoklet-generated Program code; choose a different name.");
    }

    // ── Kind transform ───────────────────────────────────────────────────────

    public override string ApplyKindTransform(string code) => Options.Kind switch
    {
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump();\n",
        RpadKind.Program => $"{RpadKindTransform.ProgramPrefixFor(code, ParseOptions())}{ProgramBodyProjection.Create(code, ParseOptions()).Text}{RpadKindTransform.ProgramSuffixFor(code, ParseOptions())}",
        _ => RpadKindTransform.StatementsFor(code, ParseOptions()),
    };

    // Program with hoisted leading using/extern-alias directives: the generated file starts with the
    // front-matter imports, then the directives (their first line glued after the imports, the last one
    // shared with the call statement and class header unless it ends a conditional block), then the
    // wrapper's opening brace line, then the user's own first line. Directive lines map back to where
    // they were written; the call statement, header and brace are generated.
    protected override (int Line, int Column)? MapToUserPosition(string userCode, int line, int column)
    {
        var parseOptions = ParseOptions();
        if (Options.Kind != RpadKind.Program || ProgramEntryPointDetector.IsCompiledAsWritten(userCode, parseOptions))
            return base.MapToUserPosition(userCode, line, column);
        var projection = ProgramBodyProjection.Create(userCode, parseOptions);
        if (projection.LeadingDirectives.Length != 0)
        {
            var directiveLines = projection.LeadingDirectives.Count(c => c == '\n') + 1;
            var index = line - Options.Usings.Count - 1;
            if (index >= 0 && index < directiveLines)
            {
                var start = SourceText.From(userCode).Lines.GetLinePosition(projection.LeadingDirectivesStart);
                var lastLine = projection.LeadingDirectives[(projection.LeadingDirectives.LastIndexOf('\n') + 1)..];
                // What follows the directives on their last line (the call statement) is generated.
                if (index == directiveLines - 1 && column > lastLine.TrimEnd('\r').Length)
                    return null;
                return (start.Line + 1 + index, index == 0 ? column + start.Character : column);
            }
        }
        return base.MapToUserPosition(userCode, line, column);
    }

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
    // one (a program compiled as written has none), inserts 2 new lines before the user's own first
    // line (the call statement itself has no trailing newline — glued onto the same physical line as
    // the wrapper class's own header — but the class header and its opening brace each occupy their
    // own line, regardless of which call variant was actually picked), plus one more line for each
    // embedded newline in any leading using/extern-alias directives the projection hoisted ahead of
    // the call statement (ProgramBodyProjection.LeadingDirectives; see RpadKindTransform.ProgramLineOffset).
    // ApplyKindTransform's own suffix (the class's closing brace) comes AFTER the user's code and so
    // never affects this offset.
    // A Program compiled as written (no top-level statements) gets no prefix at all.
    public override int CalcKindOffset(string code) => Options.Usings.Count + (Options.Kind switch
    {
        RpadKind.Expression => 1,
        RpadKind.Program => RpadKindTransform.ProgramLineOffset(code, ParseOptions()),
        _ => 0,
    });
}
