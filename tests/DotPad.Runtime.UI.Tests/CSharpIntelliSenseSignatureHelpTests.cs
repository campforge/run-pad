using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ObjectExtensions.Dump is deliberately many small overloads (Dump(), Dump(label), Dump(depth),
// Dump(label, depth), Dump(DumpOptions)) instead of one method with a pile of nullable parameters —
// see ObjectExtensions.cs. This guards that design choice at the signature-help level: whatever
// overload CSharpIntelliSenseService's custom signature-help resolves to for "x.Dump(" should never
// balloon back into a many-parameter wall.
public class CSharpIntelliSenseSignatureHelpTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task SignatureHelp_ForDump_Should_NeverShowAManyParameterOverload()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.All(result!.Items, item => Assert.True(item.Parameters.Count <= 3,
            $"Expected only small Dump overloads, but got: {string.Concat(item.Signature.Select(s => s.Text))}"));
    }

    [Fact]
    public async Task Completions_Should_OfferDump()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Du";
        var position = code.Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "Dump");
    }

    // TODO.md #65: EditorTabItemControl uses IsArgumentSlotEmpty to redirect an explicit Ctrl+Space
    // request to overloads instead of a completion list, right after "(" — this is the underlying
    // signal that decision is based on.
    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotEmpty_RightAfterOpenParen()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsArgumentSlotEmpty);
    }

    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotNotEmpty_WhenPartiallyTyped()
    {
        using var service = CreateService();
        // "x" here is a partially-typed identifier argument (not a string literal — signature help
        // deliberately returns null while inside a string/char literal, a separate, pre-existing
        // check unrelated to this test), same shape as e.g. typing a variable name into the call.
        const string code = "var x = 42;\nx.Dump(x";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsArgumentSlotEmpty);
    }

    // Right after a "," with nothing typed for the next argument yet — same "fresh slot" as right
    // after "(", not just the first argument.
    [Fact]
    public async Task SignatureHelp_Should_ReportArgumentSlotEmpty_RightAfterComma()
    {
        using var service = CreateService();
        const string code = "var x = 42;\nx.Dump(\"label\", ";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsArgumentSlotEmpty);
    }

    // TODO.md #53 remainder: the highlighted overload used to be picked by argument COUNT alone —
    // Foo(1, "x") would highlight Foo(int, int) over Foo(int, string) whenever the former happened to
    // sort first, even though only the latter actually type-checks.
    [Fact]
    public async Task SignatureHelp_Should_SelectTheOverload_WhoseParameterTypesMatchTheTypedArguments()
    {
        using var service = CreateService();
        // The string argument is typed FIRST and the cursor sits after the second (numeric) one —
        // SelectBestOverloadIndex checks every already-parsed argument regardless of where the caret
        // currently is, and ending on a numeric literal avoids IsInsideStringOrCharLiteral's own
        // position-1 boundary fallback (meant for an in-progress, unterminated literal) misfiring
        // right at a just-closed string's last quote.
        const string code = "class C\n{\n    void Foo(int a, int b) { }\n    void Foo(string a, int b) { }\n}\n" +
            "var c = new C();\nc.Foo(\"x\", 1";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var selected = result!.Items[result.SelectedItemIndex];
        Assert.Contains("string a", string.Concat(selected.Signature.Select(s => s.Text)));
    }

    [Fact]
    public async Task SignatureHelp_Should_SelectTheIntIntOverload_WhenBothArgumentsAreInts()
    {
        using var service = CreateService();
        const string code = "class C\n{\n    void Foo(int a, int b) { }\n    void Foo(int a, string b) { }\n}\n" +
            "var c = new C();\nc.Foo(1, 2";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var selected = result!.Items[result.SelectedItemIndex];
        Assert.Contains("int b", string.Concat(selected.Signature.Select(s => s.Text)));
    }

    // No candidate's parameter types match perfectly (bool isn't implicitly convertible to either int
    // or string) — must fall back to the pre-existing arity-only match rather than throwing or
    // returning an out-of-range index.
    [Fact]
    public async Task SignatureHelp_Should_FallBackToArityOnly_WhenNoOverloadTypeChecksPerfectly()
    {
        using var service = CreateService();
        const string code = "class C\n{\n    void Foo(int a, int b) { }\n    void Foo(int a, string b) { }\n}\n" +
            "var c = new C();\nc.Foo(1, true";
        var position = code.Length;

        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.InRange(result!.SelectedItemIndex, 0, result.Items.Count - 1);
    }
}
