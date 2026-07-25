using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Implementation" (Ctrl+F12, natural follow-up to Go to Definition/F12 from the
// eighty-seventh pass) — CSharpIntelliSenseService.GetImplementationsAsync. A single script's own
// AdhocWorkspace only ever contains the script itself, so every result is an in-document offset —
// no decompiling branch needed here, unlike GetDefinitionAsync.
public class CSharpIntelliSenseGoToImplementationTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_FindTheSingleImplementation_OfAnInterfaceMember()
    {
        using var service = CreateService();
        const string code = """
            interface ILogger { void Write(string message); }
            class FileLogger : ILogger { public void Write(string message) { } }
            """;
        var position = code.IndexOf("Write(string message);", StringComparison.Ordinal);

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result!);
        // The offset must land inside FileLogger's own Write method text, not the interface's.
        var implMethodStart = code.IndexOf("public void Write", StringComparison.Ordinal);
        Assert.InRange(result![0].Offset, implMethodStart, implMethodStart + "public void Write".Length);
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_FindEveryImplementation_OfAnInterfaceType()
    {
        using var service = CreateService();
        const string code = """
            interface IShape { }
            class Circle : IShape { }
            class Square : IShape { }
            """;
        var position = code.IndexOf("IShape", StringComparison.Ordinal); // first occurrence = the interface's own declaration

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, r => r.DisplayName == "Circle");
        Assert.Contains(result, r => r.DisplayName == "Square");
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_FindOverrides_OfAnAbstractMember()
    {
        using var service = CreateService();
        const string code = """
            abstract class Shape { public abstract double Area(); }
            class Circle : Shape { public override double Area() => 1; }
            """;
        var position = code.IndexOf("Area();", StringComparison.Ordinal);

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Contains("Area", result![0].DisplayName);
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_ReturnNull_WhenThereAreNoImplementationsInTheScript()
    {
        using var service = CreateService();
        const string code = "interface ILogger { void Write(string message); }";
        var position = code.IndexOf("Write", StringComparison.Ordinal);

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_ReturnNull_ForAnOrdinaryNonOverridableMethod()
    {
        using var service = CreateService();
        const string code = "class Foo { public void Bar() { } }\nnew Foo().Bar();";
        var position = code.IndexOf("public void Bar", StringComparison.Ordinal) + "public void ".Length;

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetImplementationsAsync_Should_ReturnNull_WhenCaretIsNotOnAnIdentifier()
    {
        using var service = CreateService();
        const string code = "var value = 1;";
        var position = code.IndexOf('=', StringComparison.Ordinal);

        var result = await service.GetImplementationsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
