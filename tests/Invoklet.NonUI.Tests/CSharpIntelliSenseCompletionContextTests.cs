using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Which kind of place each completion list was built for, and where its items came from.
public class CSharpIntelliSenseCompletionContextTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    private static async Task<SimpleCompletionList?> CompleteAsync(
        CSharpIntelliSenseService service, string code, CompletionTrigger trigger, string caretMarker = "|")
    {
        var position = code.IndexOf(caretMarker, StringComparison.Ordinal);
        code = code.Remove(position, caretMarker.Length);
        return (SimpleCompletionList?)await service.GetCompletionsAsync(code, position, trigger, TestContext.Current.CancellationToken);
    }

    public static TheoryData<string, char?, string> Contexts => new()
    {
        { "var s = \"x\";\ns.|", '.', nameof(CompletionContextKind.InstanceMember) },
        { "var s = \"x\";\ns?.|", '.', nameof(CompletionContextKind.InstanceMember) },
        { "DateTime.|", '.', nameof(CompletionContextKind.StaticMember) },
        { "System.|", '.', nameof(CompletionContextKind.NamespaceMember) },
        { "var a = new List<|", null, nameof(CompletionContextKind.TypeArgument) },
        { "Console.WriteLine(|", null, nameof(CompletionContextKind.ExpectedValue) },
        { "var x = 1;\nvar y = |", null, nameof(CompletionContextKind.Identifier) },
        { "class A { public int P { get; set; } }\nvar a = new A { | };", null, nameof(CompletionContextKind.ObjectInitializerMember) },
        { "class B { public virtual void M() { } }\nclass C : B { override | }", ' ', nameof(CompletionContextKind.OverrideStub) },
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task Completion_Should_ReportTheContextItWasBuiltFor(string code, char? typed, string expected)
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Statements);

        var trigger = typed is { } ch ? CompletionTrigger.Insertion(ch) : CompletionTrigger.Invoked;
        var list = await CompleteAsync(service, code, trigger);

        Assert.NotNull(list);
        Assert.Equal(expected, list!.Context.ToString());
    }

    [Theory]
    [InlineData("// a comment |")]
    [InlineData("var s = \"text |\";")]
    [InlineData("void Mai|() { }")]
    public async Task Completion_Should_OfferNothing_WhereNothingCompletes(string code)
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Statements);

        Assert.Null(await CompleteAsync(service, code, CompletionTrigger.Invoked));
    }

    [Fact]
    public async Task Keywords_Should_BeMarkedAsKeywords()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Statements);

        var list = await CompleteAsync(service, "var x = 1;\n|", CompletionTrigger.Invoked);

        var item = Assert.IsType<SimpleCompletionItem>(list!.Items.Single(i => i.DisplayText == "foreach"));
        Assert.Equal(CompletionSourceKind.Keyword, item.Source);
    }

    [Fact]
    public async Task InvokletExtensions_Should_WinATie_OverOtherMembers()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Statements);

        var list = await CompleteAsync(service, "var n = 1;\nn.|", CompletionTrigger.Insertion('.'));

        var dump = Assert.IsType<SimpleCompletionItem>(list!.Items.Single(i => i.DisplayText == "Dump"));
        Assert.Equal(CompletionSourceKind.Invoklet, dump.Source);
        Assert.All(list.Items.Where(i => i.DisplayText != "Dump" && i is SimpleCompletionItem { Source: not CompletionSourceKind.Invoklet }),
            other => Assert.True(other.Priority < dump.Priority, $"{other.DisplayText} {other.Priority} >= Dump {dump.Priority}"));
        // The order of the list itself is unchanged: alphabetical within the members.
        Assert.True(list.Items.ToList().FindIndex(i => i.DisplayText == "CompareTo")
            < list.Items.ToList().FindIndex(i => i.DisplayText == "Dump"));
    }
}
