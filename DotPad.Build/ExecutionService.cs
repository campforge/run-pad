using DotPad.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

using System.Reflection;
using System.Runtime.Loader;

namespace DotPad.Build;

internal sealed class ExecutionService : IExecutionService
{

	public ExecutionService()
	{
	
	}

	public async Task ExecuteAsync(string code)
	{
		SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(code);

		string assemblyName = Path.GetRandomFileName();
		var refPaths = new[] {
				typeof(object).GetTypeInfo().Assembly.Location,
				typeof(Console).GetTypeInfo().Assembly.Location,
				typeof(ObjectExtensions).GetTypeInfo().Assembly.Location,
				Path.Combine(Path.GetDirectoryName(typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly.Location), "System.Runtime.dll")
			};
		
		var references = refPaths.Select(r => MetadataReference.CreateFromFile(r)).ToArray();


		var compilation = CSharpCompilation
			.Create(assemblyName,
				syntaxTrees: new[] { syntaxTree },
				references: references,
				options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
			);
		
		using var pdbStream = new MemoryStream();
		using var peStream = new MemoryStream();
		using var xmlDocumentationStream = new MemoryStream();
		using var win32Resources = new MemoryStream();
		var result = compilation.Emit(peStream, pdbStream, xmlDocumentationStream, null, null, new EmitOptions());


		if (!result.Success)
		{
			IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
				diagnostic.IsWarningAsError ||
				diagnostic.Severity == DiagnosticSeverity.Error);

			foreach (Diagnostic diagnostic in failures)
			{
				Console.Error.WriteLine("\t{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
			}
		}
		else
		{
			peStream.Seek(0, SeekOrigin.Begin);

			//var localProcesses = Process.GetProcesses().Where(p=>p.);
			Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(peStream, pdbStream);

			// ToDo: резепук через дебаггер
			var t = new Thread(() =>
			{
				var type = assembly.GetType("RoslynCompileSample.Writer");
				var instance = assembly.CreateInstance("RoslynCompileSample.Writer");
				var meth = type.GetMember("Write").First() as MethodInfo;
				meth.Invoke(instance, new[] { "joel" });
			});

			t.Start();
		}
	}

	public async Task TerminateAsync()
	{
		throw new NotImplementedException();
	}
}

