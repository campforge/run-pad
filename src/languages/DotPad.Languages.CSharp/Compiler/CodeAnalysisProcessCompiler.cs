using System.Text;
using DotPad.Build.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using ExecutionPlatform = DotPad.Contracts.Models.ExecutionPlatform;

namespace DotPad.Languages.CSharp.Compiler;

// In-process Roslyn compiler (CSharpCompilation.Emit, no dotnet-build subprocess) — used by
// CSharpLanguageHost as a fast path for scripts with no NuGet packages and no project references
// (see ANALYSIS.md feature #16). Scripts using either fall back to DotnetProcessCompiler, which
// generates a real .csproj and shells out to the SDK's own restore/build/reference-resolution
// logic instead of reimplementing it here.
public sealed class CodeAnalysisProcessCompiler : ProcessCompilerBase
{
    private readonly string _binaryOutput;
    private readonly string _pdbOutput;
    private readonly string _configOutput;
    private readonly IReadOnlyList<string> _globalUsings;
    private readonly IReadOnlyList<string> _assemblyReferences;
    private readonly string? _targetFrameworkOverride;

    private readonly CSharpReferenceService _referenceService = new();

    public CodeAnalysisProcessCompiler(
        ExecutionPlatform executionPlatform,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName,
        IReadOnlyList<string> globalUsings,
        IReadOnlyList<string> assemblyReferences,
        string? targetFrameworkOverride) : base(executionPlatform, executionOptions, outputDir, dotPadRuntimeLib, outputName)
    {
        _binaryOutput = Path.Combine(OutputDir, $"{outputName}.dll");
        _pdbOutput = Path.Combine(OutputDir, $"{outputName}.pdb");
        _configOutput = Path.Combine(OutputDir, $"{outputName}.runtimeconfig.json");
        _globalUsings = globalUsings;
        _assemblyReferences = assemblyReferences;
        _targetFrameworkOverride = targetFrameworkOverride;
    }

    public override async Task<ProcessCompilerResult> Compilation(
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        // Prepend global usings from .rpad front matter — matches DotnetProcessCompiler, which does
        // the same before writing the source file it hands to `dotnet build`. Re-parsing (rather
        // than just tracking an offset) keeps the embedded-source PDB byte-for-byte what actually
        // got compiled, which is what the debugger displays.
        if (_globalUsings.Count > 0)
        {
            var code = (await syntaxTree.GetTextAsync(cancellationToken)).ToString();
            var usingsText = string.Join("\n", _globalUsings.Select(u => $"global using {u};"));
            code = usingsText + "\n" + code;
            var bytes = Encoding.UTF8.GetBytes(code);
            syntaxTree = CSharpSyntaxTree.ParseText(
                SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true),
                new CSharpParseOptions(),
                syntaxTree.FilePath);
        }

        var refs = _referenceService.GetDefault()
            .Concat(_assemblyReferences)
            .Append(DotPadRuntimeLib)
            .ToList();
        var outputRuntimeLib = Path.Combine(OutputDir, Path.GetFileName(DotPadRuntimeLib));

        await File.WriteAllTextAsync(_configOutput, CreateConfig(), cancellationToken);
        File.Copy(DotPadRuntimeLib, outputRuntimeLib, true);

        // Referenced assemblies need to sit next to the output binary for it to run/debug —
        // DotnetProcessCompiler gets this for free from MSBuild's CopyLocal behavior.
        foreach (var asmRef in _assemblyReferences)
        {
            if (!File.Exists(asmRef)) continue;
            var dest = Path.Combine(OutputDir, Path.GetFileName(asmRef));
            if (!string.Equals(Path.GetFullPath(asmRef), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(asmRef, dest, true);
        }

        var references = refs.Select(r => MetadataReference.CreateFromFile(r)).ToArray();

        var compilation = CSharpCompilation.Create(
            Guid.NewGuid().ToString(),
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug,
                generalDiagnosticOption: ReportDiagnostic.Default));

        // The syntax tree's SourceText was built with canBeEmbedded: true (see BuildSyntaxTree /
        // the usings-prepend re-parse above) so the debugger can show source without a separate
        // .cs file on disk — but Emit() only actually embeds it if told to explicitly via
        // EmitOptions + embeddedTexts. Omitting either reproduces a real bug this fast path had
        // never hit before, since it was unwired dead code until now: Emit() throws
        // "Value cannot be null. (Parameter 'type')" while writing debug info for a tree whose
        // SourceText claims to be embeddable but isn't actually passed through.
        var embeddedTexts = new[] { EmbeddedText.FromSource(syntaxTree.FilePath, await syntaxTree.GetTextAsync(cancellationToken)) };

        // The path-based Emit() overload doesn't accept embeddedTexts — only the stream-based one
        // does, so the output files are opened explicitly here instead.
        EmitResult result;
        await using (var peStream = File.Create(_binaryOutput))
        await using (var pdbStream = File.Create(_pdbOutput))
        {
            result = compilation.Emit(
                peStream: peStream,
                pdbStream: pdbStream,
                embeddedTexts: embeddedTexts,
                // pdbFilePath is what gets written into the PE's debug directory entry — without
                // it netcoredbg can't correlate the DLL with its PDB at all ("Symbols not found",
                // confirmed live: breakpoints/stepping silently don't work even though the binary
                // runs fine).
                options: new EmitOptions(
                    debugInformationFormat: DebugInformationFormat.PortablePdb,
                    pdbFilePath: _pdbOutput),
                cancellationToken: cancellationToken);
        }

        var errors = new List<ResultItem>();
        var warnings = new List<ResultItem>();

        foreach (var d in result.Diagnostics)
        {
            if (d.IsWarningAsError || d.Severity == DiagnosticSeverity.Error)
                errors.Add(ParseDiagnostic(d));
            else if (d.Severity == DiagnosticSeverity.Warning)
                warnings.Add(ParseDiagnostic(d));
        }

        return new ProcessCompilerResult(_binaryOutput, result.Success, errors, warnings);
    }

    public override void Dispose() { }

    private static ResultItem ParseDiagnostic(Diagnostic d)
    {
        var pos = d.Location.GetLineSpan().StartLinePosition;
        return new ResultItem(
            d.Id, "",
            new LineSymbol((uint)pos.Line, (uint)pos.Character),
            d.GetMessage());
    }

    private string CreateConfig() => $$"""
        {
            "runtimeOptions": {
            "tfm": "{{_targetFrameworkOverride ?? ExecutionPlatform.TargetFrameworkMoniker}}",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "{{ExecutionPlatform.RuntimeConfigVersion}}"
            },
            "configProperties": {
                  "System.Runtime.InteropServices.BuiltInComInterop.IsSupported": true
                }
            }
          }
        """;
}
