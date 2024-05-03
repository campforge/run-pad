using System.Text;
using DotPad.Build.Models;
using DotPad.Build.Models.Options;
using DotPad.Build.ProcessCompiler.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotPad.Build.ProcessCompiler;

public sealed class CodeAnalysisProcessCompiler : ProcessCompilerBase
{
    private readonly Encoding _encoding = Encoding.UTF8;

    private readonly string _binaryOutput;
    private readonly string _pdbOutput;
    private readonly string _configOutput;
    private readonly string _sourceCodePath;

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
        _sourceCodePath = Path.Combine(OutputDir, $"{outputName}.cs");
    }

    private ResultItem ParseDiagnostic(Diagnostic diagnostic)
    {
        var locationLine = diagnostic.Location.GetLineSpan().StartLinePosition;
        var file = "";
        Console.WriteLine($" {file}({locationLine.Line},{locationLine.Character}):" +
                          $"{diagnostic.Severity} {diagnostic.Id}: {diagnostic.GetMessage()}");

        return new ResultItem(
            diagnostic.Id,
            file,
            new LineSymbol((uint)locationLine.Line, (uint)locationLine.Character),
            diagnostic.GetMessage()
        );
    }

    public override async Task<ProcessCompilerResult> Compilation(
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {

        var refs = _referenceService.GetDefault().ToList();
        refs.Add(DotPadRuntimeLib);
        var references = refs.Select(r => MetadataReference.CreateFromFile(r)).ToArray();
    
        var syntaxRootNode = await syntaxTree.GetRootAsync(cancellationToken) as CSharpSyntaxNode;
        var encoded = CSharpSyntaxTree.Create(syntaxRootNode!, null, Path.GetFileName(_sourceCodePath), _encoding);

        var compilation = CSharpCompilation
            .Create(
                Guid.NewGuid().ToString(),
                syntaxTrees: new[] { encoded },
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.WindowsRuntimeApplication,
                    optimizationLevel: OptimizationLevel.Debug,
                    generalDiagnosticOption: ReportDiagnostic.Default
                )
            );

        var json = CreateConfig();

        await File.WriteAllTextAsync(_configOutput, json, cancellationToken);
        File.Copy(DotPadRuntimeLib, Path.Combine(OutputDir, Path.GetFileName(DotPadRuntimeLib)), true);

        var result = compilation.Emit(
            outputPath: _binaryOutput,
            pdbPath: _pdbOutput,
            cancellationToken: cancellationToken);
     
        var errors = new List<ResultItem>();
        var warnings =  new List<ResultItem>();

        foreach (var diagnostic in result.Diagnostics)
        {


            if (diagnostic.IsWarningAsError)
            {
                errors.Add(ParseDiagnostic(diagnostic));
                continue;
            }
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(ParseDiagnostic(diagnostic));
                continue;
            }
            
            if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                warnings.Add(ParseDiagnostic(diagnostic));
                continue;
            }
        }

        return new ProcessCompilerResult(_binaryOutput, result.Success, errors, warnings);
    }

    public override void Dispose()
    {
        
    }

    private string CreateConfig()
    {
        var json = @"
    {
        ""runtimeOptions"": {
        ""tfm"": """ + ExecutionPlatform.TargetFrameworkMoniker + @""",
        ""framework"": {
          ""name"": ""Microsoft.NETCore.App"",
          ""version"": """ + $"{ExecutionPlatform.Version.Major}.{ExecutionPlatform.Version.Minor}.0" + @"""
        },
        ""configProperties"": {
              ""System.Runtime.InteropServices.BuiltInComInterop.IsSupported"": true
            }
        }
      }";
        return json;
    }
}