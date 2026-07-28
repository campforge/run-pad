using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19) — CSharpIntelliSenseService.
// GetDefinitionAsync. A symbol the script itself declares resolves to an in-document offset (no
// decompiling needed, single-file scratchpad); anything from outside the script (framework/NuGet
// metadata) resolves to enough identifying info for CSharpDecompilerService (RunPad.Languages.
// DotnetCommon) to decompile it on demand — see CSharpDecompilerServiceTests.cs for that half.
public class CSharpIntelliSenseGoToDefinitionTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetDefinitionAsync_Should_ReturnInSourceOffset_ForAUserDeclaredMethod()
    {
        using var service = CreateService();
        const string code = "void Greet() { Console.WriteLine(\"hi\"); }\nGreet();";
        var position = code.IndexOf("Greet();", StringComparison.Ordinal);

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsInSource);
        Assert.Equal("Greet", code.Substring(result.Offset, "Greet".Length));
    }

    [Fact]
    public async Task GetDefinitionAsync_Should_ReturnInSourceOffset_ForALocalVariableDeclaration()
    {
        using var service = CreateService();
        const string code = "var value = 1;\nConsole.WriteLine(value);";
        var position = code.IndexOf("(value)", StringComparison.Ordinal) + 1;

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsInSource);
        Assert.Equal(code.IndexOf("value =", StringComparison.Ordinal), result.Offset);
    }

    [Fact]
    public async Task GetDefinitionAsync_Should_ReturnExternalInfo_ForAFrameworkMethod()
    {
        using var service = CreateService();
        const string code = "Console.WriteLine(\"hi\");";
        var position = code.IndexOf("WriteLine", StringComparison.Ordinal);

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsInSource);
        Assert.False(string.IsNullOrEmpty(result.AssemblyPath));
        Assert.Equal("System.Console", result.ContainingTypeFullName);
        Assert.Equal("WriteLine", result.MemberName);
    }

    // F12 directly on a TYPE name (not a member) — the definition IS the containing type, so
    // MemberName must be null (ContainingTypeFullName alone identifies the whole target).
    [Fact]
    public async Task GetDefinitionAsync_Should_ReturnExternalInfoWithNullMemberName_ForATypeNameItself()
    {
        using var service = CreateService();
        const string code = "System.Collections.Generic.List<int> list = new();";
        var position = code.IndexOf("List<int>", StringComparison.Ordinal) + 1;

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsInSource);
        Assert.Null(result.MemberName);
        Assert.Equal("System.Collections.Generic.List`1", result.ContainingTypeFullName);
    }

    // Extension methods resolve to their REDUCED form (receiver dropped from Parameters) — the
    // decompiled metadata only has the original static method with the receiver as parameter 0.
    // Regression guard: without un-reducing first, ParameterTypeNames would have 2 entries instead
    // of 3, and CSharpDecompilerService would never match the right overload.
    [Fact]
    public async Task GetDefinitionAsync_Should_UnreduceExtensionMethods_SoParameterCountIncludesTheReceiver()
    {
        using var service = CreateService();
        const string code = "1.Dump(\"label\", 2);";
        var position = code.IndexOf("Dump", StringComparison.Ordinal);

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsInSource);
        Assert.Equal("ObjectExtensions", result.ContainingTypeFullName);
        Assert.Equal("Dump", result.MemberName);
        Assert.Equal(3, result.ParameterTypeNames.Count); // receiver + label + depth
    }

    [Fact]
    public async Task GetDefinitionAsync_Should_ReturnNull_WhenCaretIsNotOnAnIdentifier()
    {
        using var service = CreateService();
        const string code = "var value = 1;";
        var position = code.IndexOf('=', StringComparison.Ordinal);

        var result = await service.GetDefinitionAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
