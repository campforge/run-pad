using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;

namespace RunPad.Languages.CSharp.Compiler;

// C# scripts through Roslyn, with the settings the SDK passes to csc for a throwaway Exe project
// (captured from a real `dotnet build -v:d`): /langversion of the target, /warn:<major>, /nullable:enable,
// /define:TRACE;DEBUG;NET...;NETx_0_OR_GREATER..., /nowarn:1701,1702, /warnaserror+:SYSLIB0011,
// /optimize-, /deterministic+, implicit usings + RunPad.Runtime; /unsafe only when the script asks for it.
public sealed class CSharpScriptCompiler(CompiledDotnetLanguageHostOptions options, ILogger logger)
    : RoslynScriptCompiler(options, logger)
{
    protected override string SourceFileExtension => CSharpProjectProfile.Instance.SourceFileExtension;
    protected override string LanguageFolder => "cs";

    protected override string CompilationSettings => Options.AllowUnsafe ? "unsafe" : "";

    protected override string FormatImport(string ns) => CSharpProjectProfile.Instance.FormatImportStatement(ns);

    protected override SyntaxTree Parse(SourceText text, string path, ScriptTarget target, CancellationToken ct) =>
        CSharpSyntaxTree.ParseText(text, ParseOptions(target), path, ct);

    // <ImplicitUsings>enable</ImplicitUsings>, the profile's <Using Include="RunPad.Runtime" /> and the
    // TargetFrameworkAttribute (AppContext.TargetFrameworkName).
    protected override string GeneratedSource(ScriptTarget target)
    {
        var builder = new StringBuilder();
        foreach (var ns in ImplicitUsings.Default.Concat(CSharpProjectProfile.Instance.DefaultImportNamespaces))
            builder.Append("global using global::").Append(ns).Append(";\n");
        builder.Append("[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(")
            .Append($"\".NETCoreApp,Version=v{target.Major}.{target.Minor}\", FrameworkDisplayName = \".NET {target.Major}.{target.Minor}\")]\n");
        return builder.ToString();
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

    internal static CSharpParseOptions ParseOptions(ScriptTarget target) => ParseOptions(target.Major, target.Minor);

    internal static CSharpParseOptions ParseOptions(int major, int minor) => new(
        LanguageVersionFor(major),
        preprocessorSymbols: ["TRACE", "DEBUG", .. FrameworkSymbols(major, minor)]);

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
