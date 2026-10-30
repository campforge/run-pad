using ICSharpCode.Decompiler.CSharp;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// What the assembly explorer reads from a .NET assembly. Every member opens the file and decompiles
/// or scans metadata, so it is slow compared with the window that shows the answer: the window
/// takes this interface, and its tests hand it ready-made answers.
/// </summary>
public interface IAssemblyBrowser
{
    AssemblyDescription DescribeAssembly(string assemblyPath);
    IReadOnlyList<AssemblyTypeEntry> EnumerateTypes(string assemblyPath);
    AssemblyTypeContents GetTypeContents(string assemblyPath, string typeFullName);
    DecompiledMemberResult DescribeResource(string assemblyPath, string resourceName);
    IReadOnlyList<AssemblySearchEntry> BuildSearchIndex(string assemblyPath);
    DefinitionTokens? ResolveDefinitionTokens(IDefinitionResult definition);

    IReadOnlyList<AssemblySearchEntry> Analyze(
        AnalysisKind kind, string targetAssemblyPath, int targetToken, IReadOnlyList<string> assemblyPaths,
        CancellationToken cancellationToken, bool includeVirtualCalls);

    DecompiledMemberResult DecompileEntity(
        string assemblyPath, int metadataToken, string title, LanguageVersion? languageVersion, int focusToken);

    DecompiledMemberResult DisassembleEntity(string assemblyPath, int metadataToken, string title);
}

/// <summary>The real thing: <see cref="CSharpDecompilerService"/> and <see cref="IlDisassembler"/>.</summary>
public sealed class DotnetAssemblyBrowser : IAssemblyBrowser
{
    public static DotnetAssemblyBrowser Instance { get; } = new();

    public AssemblyDescription DescribeAssembly(string assemblyPath) => CSharpDecompilerService.DescribeAssembly(assemblyPath);
    public IReadOnlyList<AssemblyTypeEntry> EnumerateTypes(string assemblyPath) => CSharpDecompilerService.EnumerateTypes(assemblyPath);
    public AssemblyTypeContents GetTypeContents(string assemblyPath, string typeFullName) =>
        CSharpDecompilerService.GetTypeContents(assemblyPath, typeFullName);
    public DecompiledMemberResult DescribeResource(string assemblyPath, string resourceName) =>
        CSharpDecompilerService.DescribeResource(assemblyPath, resourceName);
    public IReadOnlyList<AssemblySearchEntry> BuildSearchIndex(string assemblyPath) => CSharpDecompilerService.BuildSearchIndex(assemblyPath);
    public DefinitionTokens? ResolveDefinitionTokens(IDefinitionResult definition) => CSharpDecompilerService.ResolveDefinitionTokens(definition);

    public IReadOnlyList<AssemblySearchEntry> Analyze(
        AnalysisKind kind, string targetAssemblyPath, int targetToken, IReadOnlyList<string> assemblyPaths,
        CancellationToken cancellationToken, bool includeVirtualCalls) =>
        CSharpDecompilerService.Analyze(kind, targetAssemblyPath, targetToken, assemblyPaths, cancellationToken, includeVirtualCalls);

    public DecompiledMemberResult DecompileEntity(
        string assemblyPath, int metadataToken, string title, LanguageVersion? languageVersion, int focusToken) =>
        CSharpDecompilerService.DecompileEntity(assemblyPath, metadataToken, title, languageVersion, focusToken);

    public DecompiledMemberResult DisassembleEntity(string assemblyPath, int metadataToken, string title) =>
        IlDisassembler.DisassembleEntity(assemblyPath, metadataToken, title);
}
