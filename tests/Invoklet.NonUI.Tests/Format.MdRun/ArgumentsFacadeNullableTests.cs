using Invoklet.Format.MdRun;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Invoklet.NonUI.Tests.Format.MdRun;

// The Arguments facade goes in the script's *.Generated.cs file, which the compiler takes for
// generated code with nullable off: an optional argument's T? must not warn there (CS8669).
public class ArgumentsFacadeNullableTests
{
    [Fact]
    public void Facade_Should_CompileWithoutNullableWarnings_InAGeneratedFile()
    {
        var contract = new MdRunArgumentContract([
            new("value", "string", [], [], IsNullable: true),
            new("tags", "string[]", [], [], IsNullable: true),
            new("count", "int", [], [], IsNullable: true),
        ]);
        var tree = CSharpSyntaxTree.ParseText(contract.GenerateCSharpFacade(),
            new CSharpParseOptions(LanguageVersion.Latest), path: "InvokletQuery.Generated.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Facade", [tree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
                MetadataReference.CreateFromFile(typeof(Invoklet.Runtime.ArgumentRuntime).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var diagnostics = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();

        Assert.Empty(diagnostics);
    }
}
