using Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

// A member declared directly on the type being
// completed should render bold in the list; an inherited member (from a base class) should render
// normal weight — ICompletionItem.IsOwnMember carries that distinction from the language service down
// to DisplaySegmentRenderer.BuildListRow.
public class CSharpIntelliSenseOwnMemberTests
{
    [Fact]
    public async Task GetCompletionsAsync_Should_MarkAsOwnMember_ForAMethodDeclaredOnTheReceiverType()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class Base { }\nclass Derived : Base\n{\n    public void Bar() { }\n}\n" +
            "var d = new Derived();\nd.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            Invoklet.Contracts.IntelliSense.CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var bar = Assert.Single(result!.Items, i => i.DisplayText == "Bar");
        Assert.True(bar.IsOwnMember);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotMarkAsOwnMember_ForAnInheritedMethod()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class Base\n{\n    public void Foo() { }\n}\nclass Derived : Base { }\n" +
            "var d = new Derived();\nd.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            Invoklet.Contracts.IntelliSense.CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var foo = Assert.Single(result!.Items, i => i.DisplayText == "Foo");
        Assert.False(foo.IsOwnMember);
    }

    // The general (non-dot) branch — a class's own field referenced unqualified inside one of its
    // own methods (implicit "this") must also carry IsOwnMember, not just dot-completion.
    [Fact]
    public async Task GetCompletionsAsync_Should_MarkAsOwnMember_ForAFieldReferencedUnqualifiedInsideItsOwnType()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class C\n{\n    private int _value;\n    void Bar()\n    {\n        _val\n    }\n}\n";
        var position = code.LastIndexOf("_val", StringComparison.Ordinal) + "_val".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var value = Assert.Single(result!.Items, i => i.DisplayText == "_value");
        Assert.True(value.IsOwnMember);
    }

    [Fact]
    public async Task GetCompletionsAsync_Should_NotMarkAsOwnMember_ForAnInheritedMethodReferencedUnqualified()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class Base\n{\n    protected void Helper() { }\n}\nclass Derived : Base\n{\n" +
            "    void Bar()\n    {\n        Help\n    }\n}\n";
        var position = code.LastIndexOf("Help", StringComparison.Ordinal) + "Help".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var helper = Assert.Single(result!.Items, i => i.DisplayText == "Helper");
        Assert.False(helper.IsOwnMember);
    }

    // Regression guard: non-member kinds (types) have no "own vs inherited" notion and must always
    // default to false, never accidentally inherit true from some unrelated aggregation.
    [Fact]
    public async Task GetCompletionsAsync_Should_LeaveIsOwnMemberFalse_ForATypeCandidate()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "Str";

        var result = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var stringType = Assert.Single(result!.Items, i => i.DisplayText == "String");
        Assert.False(stringType.IsOwnMember);
    }

    // GroupByDisplayText picks one representative per name (lowest Kind ordinal) but must still OR
    // IsOwnMember across the whole group — otherwise a derived type's own overload could be masked by
    // an inherited overload of the same name winning the tie-break.
    [Fact]
    public async Task GetCompletionsAsync_Should_MarkAsOwnMember_WhenOnlyOneOfSeveralOverloadsIsOwn()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        const string code = "class Base\n{\n    public void Foo(string s) { }\n}\nclass Derived : Base\n{\n" +
            "    public void Foo(int x) { }\n}\nvar d = new Derived();\nd.";

        var result = await service.GetCompletionsAsync(code, code.Length,
            Invoklet.Contracts.IntelliSense.CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var foo = Assert.Single(result!.Items, i => i.DisplayText == "Foo");
        Assert.True(foo.IsOwnMember);
    }
}
