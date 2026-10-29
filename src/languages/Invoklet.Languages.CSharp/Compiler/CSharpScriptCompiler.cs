using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Invoklet.Format.RPad;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;

namespace Invoklet.Languages.CSharp.Compiler;

// C# scripts through Roslyn, with the settings the SDK passes to csc for a throwaway Exe project
// (captured from a real `dotnet build -v:d`): /langversion of the target, /warn:<major>, /nullable:enable,
// /define:TRACE;DEBUG;NET...;NETx_0_OR_GREATER..., /nowarn:1701,1702, /warnaserror+:SYSLIB0011,
// /optimize-, /deterministic+, implicit usings + Invoklet.Runtime; /unsafe only when the script asks for it.
public sealed class CSharpScriptCompiler(CompiledDotnetLanguageHostOptions options, ILogger logger)
    : RoslynScriptCompiler(options, logger)
{
    protected override string SourceFileExtension => CSharpProjectProfile.Instance.SourceFileExtension;
    protected override string LanguageFolder => "cs";

    protected override string CompilationSettings => Options.AllowUnsafe ? "unsafe" : "";

    protected override string FormatImport(string ns) => CSharpProjectProfile.Instance.FormatImportStatement(ns);

    protected override SyntaxTree Parse(SourceText text, string path, ScriptTarget target, CancellationToken ct) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions(target), path, ct);

    // <ImplicitUsings>enable</ImplicitUsings>, the profile's <Using Include="Invoklet.Runtime" /> and the
    // TargetFrameworkAttribute (AppContext.TargetFrameworkName).
    protected override string GeneratedSource(ScriptTarget target)
    {
        var builder = new StringBuilder();
        foreach (var ns in ImplicitUsings.Default.Concat(CSharpProjectProfile.Instance.DefaultImportNamespaces))
            builder.Append("global using global::").Append(ns).Append(";\n");
        builder.Append("[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(")
            .Append($"\".NETCoreApp,Version=v{target.Major}.{target.Minor}\", FrameworkDisplayName = \".NET {target.Major}.{target.Minor}\")]\n");
        builder.Append(RpadArgumentContract.FromJson(Options.ArgumentContractJson).GenerateCSharpFacade());
        return builder.ToString();
    }

    protected override SyntaxTree TransformScriptTree(SyntaxTree tree)
        => new ArgumentAccessRewriter().Visit(tree.GetRoot()) is { } root
            ? CSharpSyntaxTree.Create((CSharpSyntaxNode)root, (CSharpParseOptions)tree.Options,
                tree.FilePath, Encoding.UTF8)
            : tree;

    private sealed class ArgumentAccessRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax args
                && args.Expression is IdentifierNameSyntax util
                && util.Identifier.ValueText == "Util"
                && args.Name.Identifier.ValueText == "Args")
            {
                var replacement = SyntaxFactory.IdentifierName("Arguments").WithTriviaFrom(args);
                return node.WithExpression(replacement);
            }
            return base.VisitMemberAccessExpression(node);
        }
    }

    protected override Microsoft.CodeAnalysis.Compilation CreateCompilation(
        string assemblyName, IReadOnlyList<SyntaxTree> trees, IEnumerable<MetadataReference> references, ScriptTarget target) =>
        CSharpCompilation.Create(assemblyName, trees, references, new CSharpCompilationOptions(
            Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: Options.AllowUnsafe,
            nullableContextOptions: NullableContextOptions.Enable,
            warningLevel: Math.Max(4, target.Major),
            deterministic: true,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
            {
                ["CS1701"] = ReportDiagnostic.Suppress,
                ["CS1702"] = ReportDiagnostic.Suppress,
                ["SYSLIB0011"] = ReportDiagnostic.Error, // BinaryFormatter
            }));

    protected override GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators, ParseOptions parseOptions) =>
        CSharpGeneratorDriver.Create(generators, parseOptions: (CSharpParseOptions)parseOptions);

    // RpadKind.Program's wrapper (RpadKindTransform) glues a top-level call statement in front of a
    // generated class, so Roslyn always treats that top-level code as the real entry point; a static
    // Main inside the wrapper class is then a second entry-point candidate and Roslyn reports CS7022
    // on it, even though Invoklet's own trampoline is what actually calls it. Tied to Program kind and
    // the generated wrapper/trampoline structure, not to the diagnostic's message text or class name
    // alone, so Statements/Expression code cannot lose a genuine user-authored CS7022.
    protected override IEnumerable<Diagnostic> FilterDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        Options.Kind == Invoklet.Format.RPad.RpadKind.Program
            ? diagnostics.Where(d => !IsSyntheticWrapperEntryPointWarning(d))
            : diagnostics;

    // Internal rather than private: a focused test proves this structural check in isolation, against a
    // tiny in-memory compilation, without paying for a real `dotnet build` of a whole script.
    internal static bool IsSyntheticWrapperEntryPointWarning(Diagnostic d)
    {
        if (d.Id != "CS7022" || d.Location.SourceTree is not { } tree) return false;
        var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
        var enclosingClass = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        return enclosingClass?.Identifier.ValueText == RpadKindTransform.ProgramWrapperClassName
            && enclosingClass.Members.OfType<MethodDeclarationSyntax>()
                .Any(m => m.Identifier.ValueText == RpadKindTransform.TrampolineMethodName);
    }

    internal static CSharpParseOptions ParseOptions(ScriptTarget target) => ParseOptions(target.Major, target.Minor);

    internal static CSharpParseOptions ParseOptions(int major, int minor) => new(
        LanguageVersionFor(major),
        preprocessorSymbols: ["TRACE", "DEBUG", "Invoklet", .. FrameworkSymbols(major, minor)]);

    // The C# version `dotnet build` picks by default for the target framework.
    internal static LanguageVersion LanguageVersionFor(int major) => major switch
    {
        5 => LanguageVersion.CSharp9,
        6 => LanguageVersion.CSharp10,
        7 => LanguageVersion.CSharp11,
        8 => LanguageVersion.CSharp12,
        9 => LanguageVersion.CSharp13,
        10 => LanguageVersion.CSharp14,
        _ => LanguageVersion.Default,
    };
}
