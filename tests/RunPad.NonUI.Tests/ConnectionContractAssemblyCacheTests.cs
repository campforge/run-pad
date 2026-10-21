using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RunPad.Contracts.Connections;
using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class ConnectionContractAssemblyCacheTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-contract-cache-test").FullName;
    private static readonly string[] References = [typeof(object).Assembly.Location];
    private static readonly GeneratedSourceFile[] Files = [new("Message.cs", "/// <summary>A cached contract message.</summary>\npublic class Message { public int Number { get; set; } }")];
    public void Dispose() => Directory.Delete(_folder, true);

    private static Compilation Create(string name, IReadOnlyList<GeneratedSourceFile> files) =>
        CSharpCompilation.Create(name, files.Select(f => CSharpSyntaxTree.ParseText(f.Text)),
            References.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));

    [Fact]
    public void AnUnchangedContract_Should_ReuseItsBinary_AndChangedInputsShouldMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References, "net10.0;", n => Create(n, Files), ct);
        var written = File.GetLastWriteTimeUtc(path);
        var reused = ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References, "net10.0;",
            _ => throw new InvalidOperationException("A cache hit must not compile again."), ct);
        Assert.Equal(path, reused);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Contains("A cached contract message.", File.ReadAllText(Path.ChangeExtension(path, ".xml")), StringComparison.Ordinal);
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, References, "net8.0;"));
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, References, "net10.0;unsafe"));
        GeneratedSourceFile[] changed = [Files[0] with { Text = Files[0].Text.Replace("Number", "Changed") }];
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, changed, References, "net10.0;"));
        var referenceCopy = Path.Combine(_folder, "reference.dll");
        File.Copy(References[0], referenceCopy);
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, [referenceCopy], "net10.0;"));
        var copiedReference = ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, [referenceCopy], "net10.0;", n => Create(n, Files), ct);
        Assert.Equal(copiedReference, ConnectionContractAssemblyCache.Find(_folder, Files, [referenceCopy], "net10.0;"));
        File.SetLastWriteTimeUtc(referenceCopy, File.GetLastWriteTimeUtc(referenceCopy).AddSeconds(1));
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, [referenceCopy], "net10.0;"));
    }

    [Fact]
    public async Task ConcurrentBuilders_Should_PublishOneUsableArtifact_AndCancellationShouldNotPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References, "net10.0;", n => Create(n, Files), ct), ct)));
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(paths[0], ConnectionContractAssemblyCache.Find(_folder, Files, References, "net10.0;"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References,
            "cancelled", _ => throw new InvalidOperationException("Cancelled requests must not compile."), cancelled.Token));
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, References, "cancelled"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "connection-contracts"), "*.tmp*"));
    }

    [Fact]
    public void ACorruptedBinary_Should_BeRebuilt_AndInvalidSourceShouldNotBePublished()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References, "net10.0;", n => Create(n, Files), ct);
        File.WriteAllText(path, "broken cache");
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, Files, References, "net10.0;"));
        Assert.Equal(path, ConnectionContractAssemblyCache.GetOrCreate(_folder, Files, References, "net10.0;", n => Create(n, Files), ct));
        Assert.Equal(path, ConnectionContractAssemblyCache.Find(_folder, Files, References, "net10.0;"));
        GeneratedSourceFile[] invalid = [new("Invalid.cs", "public class {")];
        var error = Assert.Throws<ConnectionContractCompilationException>(() =>
            ConnectionContractAssemblyCache.GetOrCreate(_folder, invalid, References, "net10.0;", n => Create(n, invalid), ct));
        Assert.NotEmpty(error.Diagnostics);
        Assert.Null(ConnectionContractAssemblyCache.Find(_folder, invalid, References, "net10.0;"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "connection-contracts"), "*.tmp*"));
    }
}
