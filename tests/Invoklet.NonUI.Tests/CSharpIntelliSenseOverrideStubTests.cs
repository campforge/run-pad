using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Typing "override " (like VS/Rider) lists the base type's
// overridable members and, on accept, inserts a ready-to-edit stub — not just the bare method name.
public class CSharpIntelliSenseOverrideStubTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferAnAbstractMethod_WithANotImplementedExceptionStub()
    {
        using var service = CreateService();
        const string code = "abstract class Base\n{\n    public abstract void Foo();\n}\n" +
            "class Derived : Base\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var foo = Assert.Single(result!.Items, i => i.DisplayText == "Foo");
        Assert.NotNull(foo.InsertTemplate);
        Assert.Contains("public override void Foo()", foo.InsertTemplate);
        Assert.Contains("throw new NotImplementedException();", foo.InsertTemplate);
        Assert.DoesNotContain("base.Foo", foo.InsertTemplate);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferAVirtualVoidMethod_WithABaseCallStub()
    {
        using var service = CreateService();
        const string code = "class Base\n{\n    protected virtual void Bar() { }\n}\n" +
            "class Derived : Base\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var bar = Assert.Single(result!.Items, i => i.DisplayText == "Bar");
        Assert.NotNull(bar.InsertTemplate);
        Assert.Contains("protected override void Bar()", bar.InsertTemplate);
        Assert.Contains("base.Bar();", bar.InsertTemplate);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferAVirtualNonVoidMethod_WithAReturnBaseCallStub()
    {
        using var service = CreateService();
        const string code = "class Base\n{\n    public virtual int Compute() => 0;\n}\n" +
            "class Derived : Base\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var compute = Assert.Single(result!.Items, i => i.DisplayText == "Compute");
        Assert.Contains("public override int Compute()", compute.InsertTemplate);
        Assert.Contains("return base.Compute();", compute.InsertTemplate);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferAMethod_AlreadyOverriddenInTheCurrentType()
    {
        using var service = CreateService();
        const string code = "class Base\n{\n    public virtual void Bar() { }\n" +
            "    public virtual void Baz() { }\n}\n" +
            "class Derived : Base\n{\n    public override void Bar() { }\n    override \n}\n";
        var position = code.LastIndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == "Bar");
        Assert.Contains(result.Items, i => i.DisplayText == "Baz");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotOfferASealedOverride()
    {
        using var service = CreateService();
        const string code = "class Base\n{\n    public virtual void Bar() { }\n}\n" +
            "class Middle : Base\n{\n    public sealed override void Bar() { }\n}\n" +
            "class Derived : Middle\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Items, i => i.DisplayText == "Bar");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_OfferObjectsOwnVirtualMethods_WhenNoExplicitBaseClass()
    {
        using var service = CreateService();
        const string code = "class Plain\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "ToString");
        Assert.Contains(result.Items, i => i.DisplayText == "Equals");
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_ForwardRefAndOutParameters_InTheBaseCall()
    {
        using var service = CreateService();
        const string code = "class Base\n{\n    public virtual void TryIt(ref int x, out int y) { y = 0; }\n}\n" +
            "class Derived : Base\n{\n    override \n}\n";
        var position = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var tryIt = Assert.Single(result!.Items, i => i.DisplayText == "TryIt");
        Assert.Contains("ref int x, out int y", tryIt.InsertTemplate);
        Assert.Contains("base.TryIt(ref x, out y);", tryIt.InsertTemplate);
    }

    // Regression guard: ordinary completion elsewhere in the file must keep working — the new
    // "override " short-circuit must not accidentally fire when the literal word isn't there.
    [Fact]
    public async Task GetCompletionsAsync_Should_StillWork_ForOrdinaryCompletion_ElsewhereInTheFile()
    {
        using var service = CreateService();
        const string code = "class Derived\n{\n    void Bar()\n    {\n        Consol\n    }\n}\n";
        var position = code.IndexOf("Consol", StringComparison.Ordinal) + "Consol".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Console");
        Assert.All(result.Items, i => Assert.Null(i.InsertTemplate));
    }
}
