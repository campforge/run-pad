using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.VB;
using Xunit;

namespace RunPad.NonUI.Tests;

// A run is described by a plain ExecutionRequest; the .NET backend alone turns it into a host's
// options (references, macros, package sources, runtime library) and the editor's package context,
// the same way for the window and the command-line runner, C# and Visual Basic.
public sealed class ExecutionRequestTests : IDisposable
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-execution-request").FullName;
    private readonly string _script;
    private readonly string _library;

    public ExecutionRequestTests()
    {
        _script = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "scripts")).FullName, "query.rpad");
        _library = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "scripts", "lib")).FullName, "Greeter.dll");
        File.WriteAllText(_library, "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RpadFrontMatter FrontMatter() => new()
    {
        Language = RpadLanguage.Vb,
        Kind = RpadKind.Program,
        Packages = new Dictionary<string, Package> { ["Newtonsoft.Json"] = new("13.0.3") },
        Namespaces = ["System.Text"],
        References = ["lib/Greeter.dll", "$(CurrentQueryPath)/lib/Greeter.dll", "Some.Project.csproj"],
        Args = ["from", "file"],
    };

    private ExecutionRequest Request(IReadOnlyList<string>? arguments = null) =>
        ExecutionRequest.For(FrontMatter(), _script, arguments) with { Platform = Sdk, OutputDirectory = Path.Combine(_root, "out") };

    [Fact]
    public void TheRequest_Should_CarryTheFrontMatter_AsWritten()
    {
        var request = ExecutionRequest.For(FrontMatter(), _script);

        Assert.Equal(RpadKind.Program, request.Kind);
        Assert.Equal("13.0.3", request.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], request.Usings);
        Assert.Equal(["lib/Greeter.dll", "$(CurrentQueryPath)/lib/Greeter.dll", "Some.Project.csproj"], request.References);
        Assert.Equal(["from", "file"], request.Arguments);
        Assert.Equal(_script, request.CurrentQueryPath);
        // Arguments given apart (the command line) replace args:, an empty list included.
        Assert.Equal(["cli"], ExecutionRequest.For(FrontMatter(), _script, ["cli"]).Arguments);
        Assert.Empty(ExecutionRequest.For(FrontMatter(), _script, []).Arguments);
    }

    [Fact]
    public void AnUntitledScript_Should_AskWithTheNewDocumentDefaults()
    {
        var request = ExecutionRequest.For(null, null);

        Assert.Equal(RpadKind.Statements, request.Kind);
        Assert.Empty(request.Packages);
        Assert.Empty(request.Usings);
        Assert.Empty(request.References);
        Assert.Empty(request.Arguments);
        Assert.Null(request.CurrentQueryPath);
        Assert.True(request.BreakOnUncaughtExceptions);
        Assert.False(request.BreakOnAllExceptions);
    }

    // The backend resolves the references exactly as ReferencePathResolver does (relative to the
    // script, macros expanded, project references collected for the compiler's error) and fills in
    // what the request leaves to it.
    [Fact]
    public void TheBackend_Should_BuildTheHostsOptions_FromTheRequest()
    {
        var request = Request() with { RuntimePath = "/opt/dotnet/dotnet", DebuggerPath = "/opt/netcoredbg", BreakOnAllExceptions = true };

        var options = CompiledDotnetLanguageHostOptions.From(request, "/runtime/RunPad.Runtime.dll");

        var resolved = ReferencePathResolver.Resolve(request.References, _script);
        Assert.Equal([_library, _library], options.AssemblyReferences);
        Assert.Equal(resolved.Assemblies, options.AssemblyReferences);
        Assert.Equal(["Some.Project.csproj"], options.ProjectReferences);
        Assert.Empty(options.ReferenceErrors);
        Assert.Same(Sdk, options.Platform);
        Assert.Equal(request.OutputDirectory, options.OutputDirectory);
        Assert.Equal(request.OutputDirectory, options.ExecutionOptions.WorkingDirectory);
        Assert.Equal("/opt/dotnet/dotnet", options.ExecutionOptions.Runtime.RuntimePath);
        Assert.Equal("/opt/netcoredbg", options.ExecutionOptions.Debugger.DebuggerPath);
        Assert.Equal("/runtime/RunPad.Runtime.dll", options.RuntimeLibPath);
        Assert.Equal(RpadKind.Program, options.Kind);
        Assert.Equal("13.0.3", options.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], options.Usings);
        Assert.Equal(["from", "file"], options.Arguments);
        Assert.Equal(_script, options.CurrentQueryPath);
        Assert.True(options.BreakOnAllExceptions);
        Assert.True(options.BreakOnUncaughtExceptions);
        Assert.Equal(RunPadNuGetConfigStore.ExistingDefaultConfigFile, options.NuGetConfigFile);
    }

    [Fact]
    public void TheBackend_Should_DefaultToThePathsDotnet_AndNoDebugger()
    {
        var options = CompiledDotnetLanguageHostOptions.From(Request() with { WorkingDirectory = "/work" }, "RunPad.Runtime.dll");

        Assert.Equal("dotnet", options.ExecutionOptions.Runtime.RuntimePath);
        Assert.Equal("", options.ExecutionOptions.Debugger.DebuggerPath);
        Assert.Equal("/work", options.ExecutionOptions.WorkingDirectory);
    }

    // A never-saved script has no folder for a relative reference: the build reports it.
    [Fact]
    public void ARelativeReference_WithoutAScript_Should_BeAReferenceError()
    {
        var request = ExecutionRequest.For(FrontMatter(), null) with { Platform = Sdk, OutputDirectory = _root };

        var options = CompiledDotnetLanguageHostOptions.From(request, "RunPad.Runtime.dll");

        Assert.Empty(options.AssemblyReferences);
        Assert.Contains(options.ReferenceErrors, e => e.Contains("'lib/Greeter.dll' is relative to the script's folder", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunWithoutAnSdkOrABuildFolder_Should_BeRefused()
    {
        Assert.Throws<ArgumentException>(() => CompiledDotnetLanguageHostOptions.From(Request() with { Platform = null }, "r.dll"));
        Assert.Throws<ArgumentException>(() => CompiledDotnetLanguageHostOptions.From(Request() with { OutputDirectory = null }, "r.dll"));
    }

    // The editor reads the same request with the same references, target and dotnet.
    [Fact]
    public void TheEditorsContext_Should_ReadTheRequest_AsTheBuildDoes()
    {
        var request = Request() with { RuntimePath = "/opt/dotnet/dotnet" };

        var context = Assert.IsType<RunPad.Contracts.IntelliSense.PackageReferenceContext>(DotnetPackageReferenceContext.For(request));

        Assert.Equal("net10.0", context.TargetFramework);
        Assert.Equal(request.OutputDirectory, context.OutputDirectory);
        Assert.Equal("/opt/dotnet/dotnet", context.DotnetExecutable);
        Assert.Equal(CompiledDotnetLanguageHostOptions.From(request, "r.dll").AssemblyReferences, context.AssemblyReferences);
        Assert.Equal(RunPadNuGetConfigStore.ExistingDefaultConfigFile, context.NuGetConfigFile);
        Assert.Null(DotnetPackageReferenceContext.For(request with { Platform = null }));
    }

    // Each language builds its options with the runtime library its locator names, and its host names
    // the source file it writes — the one breakpoints and the debugger refer to.
    [Fact]
    public void EachLanguage_Should_BuildItsHost_WithItsRuntimeLibrary_AndItsSourceFile()
    {
        var locator = new FixedLocator("/runtime/RunPad.Runtime.dll");
        var csharp = new CSharpLanguageHostFactory(NullLoggerFactory.Instance, locator);
        var vb = new VbLanguageHostFactory(NullLoggerFactory.Instance, locator);
        var request = Request();

        Assert.Equal("/runtime/RunPad.Runtime.dll", csharp.CreateOptions(request).RuntimeLibPath);
        Assert.Equal("/runtime/RunPad.Runtime.dll", vb.CreateOptions(request).RuntimeLibPath);

        var registry = new LanguageHostRegistry();
        registry.Register(csharp);
        registry.Register(vb);
        var output = request.OutputDirectory!;
        Assert.Equal(Path.Combine(output, LanguageHostOptionsDefaults.OutputName + ".cs"), registry.Create("csharp", request).SourceFilePath);
        Assert.Equal(Path.Combine(output, LanguageHostOptionsDefaults.OutputName + ".vb"), registry.Create("vb", request).SourceFilePath);
        Assert.Equal(".cs", registry.GetEditorProfile("csharp").SourceFileExtension);
        Assert.Equal(".vb", registry.GetEditorProfile("vb").SourceFileExtension);
    }

    [Fact]
    public void ALanguageWithoutAnExecutionAdapter_Should_SayIt()
    {
        var registry = new LanguageHostRegistry();
        registry.Register(new NoAdapter());

        var error = Assert.Throws<NotSupportedException>(() => registry.Create("plain", Request()));
        Assert.Contains("Plain", error.Message, StringComparison.Ordinal);
    }

    private sealed class FixedLocator(string path) : IRuntimeReferenceLocator
    {
        public string GetRuntimeAssemblyPath() => path;
    }

    private sealed class NoAdapter : ILanguageHostFactory
    {
        public string Language => "plain";
        public string DisplayName => "Plain";
        public LanguageHostCapabilities Capabilities => LanguageHostCapabilities.Run;
        public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile { get; } = new();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => throw new NotSupportedException();
    }
}
