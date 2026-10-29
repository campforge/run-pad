using System.Collections.Immutable;
using System.Text;
using Invoklet.Format.MdRun;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.VB;

// VB scripts through Roslyn, with the settings the SDK passes to vbc for a throwaway Exe project
// (captured from a real `dotnet build -v:d`, net8.0):
//   /imports: the profile's + Microsoft.VisualBasic,System,System.Collections,System.Collections.Generic,
//             System.Diagnostics,System.Linq,System.Xml.Linq,System.Threading.Tasks
//   /optionstrict:custom /optioninfer+ /optionexplicit+ /optioncompare:Binary /rootnamespace:<name>
//   /define:CONFIG="Debug",DEBUG=-1,TRACE=-1,PLATFORM="AnyCPU",_MyType="Empty",NET=-1,...
//   /nowarn:41999,42016,42017,42018,42019,42020,42021,42022,42032,42036 /warnaserror+:SYSLIB0011
//   /vbruntime:<targeting pack>\Microsoft.VisualBasic.dll /optimize- /deterministic+
public sealed class VbScriptCompiler(CompiledDotnetLanguageHostOptions options, ILogger logger)
    : RoslynScriptCompiler(options, logger)
{
    // Microsoft.NET.Sdk.VisualBasic's default <Import> items.
    private static readonly string[] SdkImports =
    [
        "Microsoft.VisualBasic", "System", "System.Collections", "System.Collections.Generic",
        "System.Diagnostics", "System.Linq", "System.Xml.Linq", "System.Threading.Tasks",
    ];

    private static readonly string[] NoWarn =
        ["BC41999", "BC42016", "BC42017", "BC42018", "BC42019", "BC42020", "BC42021", "BC42022", "BC42032", "BC42036"];

    protected override string SourceFileExtension => VbProjectProfile.Instance.SourceFileExtension;
    protected override string LanguageFolder => "vb";

    protected override string FormatImport(string ns) => VbProjectProfile.Instance.FormatImportStatement(ns);

    protected override SyntaxTree Parse(SourceText text, string path, ScriptTarget target, CancellationToken ct) =>
        VisualBasicSyntaxTree.ParseText(text, ParseOptions(target), path, ct);

    protected override string GeneratedSource(ScriptTarget target) =>
        $"<Assembly: Global.System.Runtime.Versioning.TargetFrameworkAttribute(\".NETCoreApp,Version=v{target.Major}.{target.Minor}\", " +
        $"FrameworkDisplayName:=\".NET {target.Major}.{target.Minor}\")>\n" +
        MdRunArgumentContract.FromJson(Options.ArgumentContractJson).GenerateVisualBasicFacade();

    protected override SyntaxTree TransformScriptTree(SyntaxTree tree)
        => new ArgumentAccessRewriter().Visit(tree.GetRoot()) is { } root
            ? VisualBasicSyntaxTree.Create((VisualBasicSyntaxNode)root, (VisualBasicParseOptions)tree.Options,
                tree.FilePath, Encoding.UTF8)
            : tree;

    private sealed class ArgumentAccessRewriter : VisualBasicSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax args
                && args.Expression is IdentifierNameSyntax util
                && util.Identifier.ValueText.Equals("Util", StringComparison.OrdinalIgnoreCase)
                && args.Name.Identifier.ValueText.Equals("Args", StringComparison.OrdinalIgnoreCase))
            {
                var replacement = SyntaxFactory.IdentifierName("Arguments").WithTriviaFrom(args);
                return node.WithExpression(replacement);
            }
            return base.VisitMemberAccessExpression(node);
        }
    }

    protected override Microsoft.CodeAnalysis.Compilation CreateCompilation(
        string assemblyName, IReadOnlyList<SyntaxTree> trees, IEnumerable<MetadataReference> references, ScriptTarget target)
    {
        var imports = VbProjectProfile.Instance.DefaultImportNamespaces.Concat(SdkImports)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(GlobalImport.Parse);
        var diagnosticOptions = NoWarn.ToDictionary(id => id, _ => ReportDiagnostic.Suppress);
        diagnosticOptions["SYSLIB0011"] = ReportDiagnostic.Error; // BinaryFormatter

        return VisualBasicCompilation.Create(assemblyName, trees, references, new VisualBasicCompilationOptions(
            Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            rootNamespace: assemblyName,
            globalImports: imports,
            optionStrict: OptionStrict.Custom,
            optionInfer: true,
            optionExplicit: true,
            optionCompareText: false,
            embedVbCoreRuntime: false,
            optimizationLevel: OptimizationLevel.Debug,
            deterministic: true,
            parseOptions: ParseOptions(target),
            specificDiagnosticOptions: diagnosticOptions));
    }

    protected override GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators, ParseOptions parseOptions) =>
        VisualBasicGeneratorDriver.Create(generators, parseOptions: (VisualBasicParseOptions)parseOptions);

    internal static VisualBasicParseOptions ParseOptions(ScriptTarget target)
    {
        var symbols = new List<KeyValuePair<string, object>>
        {
            new("CONFIG", "Debug"),
            new("DEBUG", -1),
            new("TRACE", -1),
            new("Invoklet", -1),
            new("PLATFORM", "AnyCPU"),
            new("_MyType", "Empty"),
        };
        symbols.AddRange(FrameworkSymbols(target.Major, target.Minor).Select(s => new KeyValuePair<string, object>(s, -1)));
        return new VisualBasicParseOptions(LanguageVersion.Default, preprocessorSymbols: symbols);
    }
}
