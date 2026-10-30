using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.NonUI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// GroupBy(DisplayText).First() collapses every overload of a method
// down to a single completion item (one representative per name, same as before this fix) — but gave
// no indication there was anything else under that name at all. ICompletionItem.OverloadCount now
// carries how many Method-kind candidates shared the DisplayText, so the UI can annotate the row with
// "(+N overloads)" instead of silently hiding the rest.
public class CSharpIntelliSenseOverloadCountTests
{
    [Fact]
    public async Task GetCompletionsAsync_Should_SetOverloadCountAboveOne_ForAMethodWithSeveralOverloads()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "Console.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var writeLine = Assert.Single(result!.Items, i => i.DisplayText == "WriteLine");
        Assert.Equal(CompletionItemKind.Method, writeLine.Kind);
        Assert.True(writeLine.OverloadCount > 1);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_LeaveOverloadCountAtOne_ForAMethodWithASingleOverload()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class D\n{\n    public void Baz() { }\n}\nvar d = new D();\nd.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var baz = Assert.Single(result!.Items, i => i.DisplayText == "Baz");
        Assert.Equal(1, baz.OverloadCount);
    }

    // The same grouping (and OverloadCount) applies to the general in-scope-symbols list, not just
    // member access — a class's own overloaded methods are visible unqualified inside its own body.
    [Fact]
    public async Task GetCompletionsAsync_Should_SetOverloadCount_ForOwnOverloadedMethods_ReferencedUnqualified()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C\n{\n    void Foo() { }\n    void Foo(int x) { }\n" +
            "    void Bar()\n    {\n        Fo\n    }\n}\n";
        var position = code.LastIndexOf("Fo", StringComparison.Ordinal) + 2;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var foo = Assert.Single(result!.Items, i => i.DisplayText == "Foo");
        Assert.Equal(2, foo.OverloadCount);
    }

    // Regression guard: non-Method kinds sharing a name with something else (rare, but ToItem/
    // ScopeRank already picks the lowest-Kind survivor) must not get an OverloadCount from unrelated
    // candidates — only Method-kind candidates in the group are counted.
    [Fact]
    public async Task GetCompletionsAsync_Should_LeaveOverloadCountAtOne_ForATypeWithNoOverloads()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "Str";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var stringType = Assert.Single(result!.Items, i => i.DisplayText == "String");
        Assert.Equal(1, stringType.OverloadCount);
    }
}
