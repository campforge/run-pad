using System.Text;
using DotPad.Build.Core.Models;
using Microsoft.CodeAnalysis;
using ExecutionPlatform = DotPad.Contracts.Models.ExecutionPlatform;
using Microsoft.CodeAnalysis.CSharp;

namespace DotPad.Languages.CSharp.Compiler;

public sealed class CodeAnalysisProcessCompiler : ProcessCompilerBase
{
    private readonly string _binaryOutput;
    private readonly string _pdbOutput;
    private readonly string _configOutput;

    private readonly CSharpReferenceService _referenceService = new();

    public CodeAnalysisProcessCompiler(
        ExecutionPlatform executionPlatform,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName) : base(executionPlatform, executionOptions, outputDir, dotPadRuntimeLib, outputName)
    {
        _binaryOutput = Path.Combine(OutputDir, $"{outputName}.dll");
        _pdbOutput = Path.Combine(OutputDir, $"{outputName}.pdb");
        _configOutput = Path.Combine(OutputDir, $"{outputName}.runtimeconfig.json");
    }

    public override async Task<ProcessCompilerResult> Compilation(
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var refs = _referenceService.GetDefault().Append(DotPadRuntimeLib).ToList();
        var outputRuntimeLib = Path.Combine(OutputDir, Path.GetFileName(DotPadRuntimeLib));

        await File.WriteAllTextAsync(_configOutput, CreateConfig(), cancellationToken);
        File.Copy(DotPadRuntimeLib, outputRuntimeLib, true);

        var references = refs.Select(r => MetadataReference.CreateFromFile(r)).ToArray();

        var compilation = CSharpCompilation.Create(
            Guid.NewGuid().ToString(),
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug,
                generalDiagnosticOption: ReportDiagnostic.Default));

        var result = compilation.Emit(
            outputPath: _binaryOutput,
            pdbPath: _pdbOutput,
            cancellationToken: cancellationToken);

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
            "tfm": "{{ExecutionPlatform.TargetFrameworkMoniker}}",
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
